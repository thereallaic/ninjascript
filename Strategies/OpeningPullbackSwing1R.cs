#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =====================================================================================
// OpeningPullbackSwing1R  —  Variante 3, NinjaTrader 8.1
//
// Unterschied zu OpeningPullback2R:
//   * Stop liegt unter dem GEFORMTEN SWING-LOW (Long) bzw. ueber dem geformten
//     SWING-HIGH (Short) — also unter/ueber dem tatsaechlichen Wick des Pullbacks,
//     nicht auf dem Close der letzten Gegenkerze.
//   * Take-Profit standardmaessig 1R statt 2R.
//
// Regelwerk:
//  1. Referenzkurs = Open der 1-Minuten-Kerze um 09:00.
//  2. Die ersten 5 Kerzen (09:00–09:05) werden nur beobachtet.
//     Schlusskurs der 5. Kerze > Referenz  -> Long-Bias
//     Schlusskurs der 5. Kerze < Referenz  -> Short-Bias
//  3. Long:  Einstieg per Market beim Schluss der ERSTEN GRUENEN Kerze nach dem Fenster.
//     Short: Einstieg per Market beim Schluss der ERSTEN ROTEN  Kerze nach dem Fenster.
//  4. Swing: ab Ende des Fensters wird das tiefste Low bzw. hoechste High aller Kerzen
//     bis EINSCHLIESSLICH der Signalkerze mitgefuehrt — das ist das "geformte" Extrem.
//     Stop (Long)  = Swing-Low  − Offset
//     Stop (Short) = Swing-High + Offset
//  5. R = Distanz Einstieg->Stop.  Take-Profit = Einstieg +/- RewardMultiple * R (1R).
//  6. Maximal 1 Trade pro Tag. Kein Einstieg mehr nach der Cutoff-Zeit.
//
// Hinweis: Da der Stop unter dem Wick liegt, ist R hier systematisch groesser als bei
// OpeningPullback2R. In Kombination mit dem 1R-Ziel ergibt das ein anderes Profil:
// hoehere Trefferquote, kleinerer Gewinn je Treffer. Genau der Vergleich lohnt.
//
// Wichtig: 1-Minuten-Datenserie verwenden. Zeitzone Berlin in NT einstellen
// (Tools > Options > General > Time zone).
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class OpeningPullbackSwing1R : Strategy
	{
		private const string SignalLong  = "OPS1R_Long";
		private const string SignalShort = "OPS1R_Short";

		private DateTime currentDay = DateTime.MinValue;
		private double   openPrice;          // Open der 09:00-Kerze
		private int      windowBarCount;     // Anzahl verarbeiteter Anfangskerzen
		private double   lastWindowClose;    // Close der letzten Kerze im Fenster
		private double   swingLow;           // tiefstes Low des geformten Pullbacks
		private double   swingHigh;          // hoechstes High des geformten Pullbacks
		private int      bias;               // +1 Long, -1 Short, 0 kein Trade
		private bool     biasDecided;
		private bool     entryDone;          // heute wurde eingestiegen bzw. der Tag ist abgehakt
		private double   stopPrice;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Opening-Strategie Variante 3: Richtung aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, Einstieg auf der ersten Kerze in Bias-Richtung, Stop unter dem geformten Swing-Low bzw. ueber dem Swing-High, Ziel = 1R.";
				Name                            = "OpeningPullbackSwing1R";
				Calculate                       = Calculate.OnBarClose;
				EntriesPerDirection             = 1;
				EntryHandling                   = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy    = true;   // Sicherheitsnetz: flatten zum Sessionende
				ExitOnSessionCloseSeconds       = 30;
				IsFillLimitOnTouch              = false;
				MaximumBarsLookBack             = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution             = OrderFillResolution.Standard;
				Slippage                        = 0;
				StartBehavior                   = StartBehavior.WaitUntilFlat;
				TimeInForce                     = TimeInForce.Gtc;
				TraceOrders                     = false;
				RealtimeErrorHandling           = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling              = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade             = 5;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Parameter-Defaults
				OpenHour              = 9;
				OpenMinute            = 0;
				InitialBars           = 5;
				CutoffHour            = 10;
				CutoffMinute          = 0;
				RewardMultiple        = 1;      // <-- 1R statt 2R
				StopOffsetTicks       = 2;
				IncludeWindowInSwing  = false;
				MinRiskTicks          = 0;
				StrictFirstSignal     = false;
				UseFixedRisk          = true;
				RiskPerTrade          = 100;
				MaxContracts          = 50;
				Contracts             = 1;
			}
			else if (State == State.DataLoaded)
			{
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
					Log(Name + ": Bitte eine 1-Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + "). Die Logik setzt 1-Min-Kerzen voraus.", LogLevel.Warning);
			}
		}

		// Kontraktzahl so, dass 1R moeglichst genau dem gewuenschten Geldrisiko entspricht.
		// riskPerUnit = Kursdistanz Einstieg->Stop. Rueckgabe 0 => Trade auslassen.
		private int CalcQuantity(double riskPerUnit)
		{
			if (!UseFixedRisk)
				return Contracts;

			double pointValue = Instrument.MasterInstrument.PointValue; // Waehrung je Punkt (FDXS: 1 EUR)
			if (pointValue <= 0 || riskPerUnit <= 0)
				return 0;

			int qty = (int)Math.Floor(RiskPerTrade / (riskPerUnit * pointValue));
			return Math.Min(qty, MaxContracts);
		}

		private void ResetDay(DateTime day)
		{
			currentDay      = day;
			openPrice       = 0;
			windowBarCount  = 0;
			lastWindowClose = 0;
			swingLow        = double.MaxValue;
			swingHigh       = double.MinValue;
			bias            = 0;
			biasDecided     = false;
			entryDone       = false;
			stopPrice       = 0;
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			// Neuer Handelstag -> Zustand zuruecksetzen
			if (Time[0].Date != currentDay)
				ResetDay(Time[0].Date);

			TimeSpan barClose    = Time[0].TimeOfDay;                                   // NT8: Zeitstempel = Kerzenschluss
			TimeSpan windowStart = new TimeSpan(OpenHour, OpenMinute, 0);               // 09:00:00
			TimeSpan windowEnd   = windowStart + TimeSpan.FromMinutes(InitialBars);     // 09:05:00
			TimeSpan cutoff      = new TimeSpan(CutoffHour, CutoffMinute, 0);

			bool isGreen = Close[0] > Open[0];
			bool isRed   = Close[0] < Open[0];

			// ---------- Phase 1: Beobachtungsfenster (Kerzen 1..InitialBars) ----------
			if (barClose > windowStart && barClose <= windowEnd)
			{
				if (windowBarCount == 0)
					openPrice = Open[0]; // Eroeffnungskurs 09:00

				windowBarCount++;
				lastWindowClose = Close[0];

				if (IncludeWindowInSwing)
				{
					swingLow  = Math.Min(swingLow,  Low[0]);
					swingHigh = Math.Max(swingHigh, High[0]);
				}
				return; // im Fenster wird nie gehandelt
			}

			// ---------- Phase 2: Richtungsentscheid nach der letzten Fensterkerze ----------
			if (!biasDecided && barClose > windowEnd)
			{
				biasDecided = true;

				if (windowBarCount == 0 || openPrice == 0)
					entryDone = true;                    // keine Daten im Fenster -> kein Trade
				else if (lastWindowClose > openPrice)
					bias = 1;
				else if (lastWindowClose < openPrice)
					bias = -1;
				else
					entryDone = true;                    // exakt auf dem Open -> kein Trade
			}

			if (entryDone || bias == 0)
				return;

			// Cutoff: letzte gueltige Signalkerze schliesst spaetestens zur Cutoff-Zeit
			if (barClose > cutoff)
			{
				entryDone = true;
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// ---------- Phase 3: Swing fortschreiben — INKLUSIVE der Signalkerze ----------
			// Reihenfolge ist entscheidend: das Extrem der Signalkerze selbst gehoert zum
			// geformten Swing, deshalb wird hier vor der Einstiegspruefung aktualisiert.
			swingLow  = Math.Min(swingLow,  Low[0]);
			swingHigh = Math.Max(swingHigh, High[0]);

			// ---------- Phase 4: Einstieg auf der ersten Signalkerze ----------
			if (bias == 1 && isGreen)
			{
				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(swingLow - StopOffsetTicks * TickSize);
				double risk = Close[0] - stopPrice;

				if (risk < Math.Max(1, MinRiskTicks) * TickSize)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				int qtyLong = CalcQuantity(risk);
				if (qtyLong < 1) // R zu gross fuer das Geldrisiko -> Trade auslassen
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				SetStopLoss(SignalLong, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalLong, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * risk));
				EnterLong(qtyLong, SignalLong);
				entryDone = true;
				return;
			}

			if (bias == -1 && isRed)
			{
				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(swingHigh + StopOffsetTicks * TickSize);
				double risk = stopPrice - Close[0];

				if (risk < Math.Max(1, MinRiskTicks) * TickSize)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				int qtyShort = CalcQuantity(risk);
				if (qtyShort < 1)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				SetStopLoss(SignalShort, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalShort, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] - RewardMultiple * risk));
				EnterShort(qtyShort, SignalShort);
				entryDone = true;
				return;
			}
		}

		// Ziel exakt auf Basis des tatsaechlichen Fills nachjustieren:
		// R = |Fill - Stop|, Take-Profit = Fill +/- RewardMultiple * R
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null)
				return;
			if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
				return;

			if (execution.Order.Name == SignalLong)
			{
				double risk = price - stopPrice;
				if (risk > 0)
					SetProfitTarget(SignalLong, CalculationMode.Price,
						Instrument.MasterInstrument.RoundToTickSize(price + RewardMultiple * risk));
			}
			else if (execution.Order.Name == SignalShort)
			{
				double risk = stopPrice - price;
				if (risk > 0)
					SetProfitTarget(SignalShort, CalculationMode.Price,
						Instrument.MasterInstrument.RoundToTickSize(price - RewardMultiple * risk));
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Eroeffnung – Stunde", Description = "Stunde des Referenz-Opens (lokale NT-Zeitzone)", Order = 1, GroupName = "01 Zeiten")]
		public int OpenHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Eroeffnung – Minute", Order = 2, GroupName = "01 Zeiten")]
		public int OpenMinute { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Anzahl Anfangskerzen", Description = "Beobachtungsfenster in 1-Min-Kerzen (Standard 5)", Order = 3, GroupName = "01 Zeiten")]
		public int InitialBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Einstiegs-Cutoff – Stunde", Description = "Nach dieser Zeit keine neuen Einstiege mehr", Order = 4, GroupName = "01 Zeiten")]
		public int CutoffHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Einstiegs-Cutoff – Minute", Order = 5, GroupName = "01 Zeiten")]
		public int CutoffMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 10)]
		[Display(Name = "Reward-Multiple (R)", Description = "Take-Profit = Einstieg +/- Multiple * R (Standard 1 in dieser Variante)", Order = 10, GroupName = "02 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop-Offset (Ticks)", Description = "Puffer unter dem Swing-Low bzw. ueber dem Swing-High. 0 = exakt auf dem Extrem (FDXS: 1 Tick = 1 Punkt).", Order = 11, GroupName = "02 Risiko")]
		public int StopOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Festes Geldrisiko je Trade", Description = "True (Standard): Kontraktzahl wird so berechnet, dass 1R dem Betrag unten entspricht. False: feste Kontraktzahl.", Order = 12, GroupName = "02 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade (1R)", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den 1R kosten darf. FDXS rechnet in EUR -> 100 = 100 EUR.", Order = 13, GroupName = "02 Risiko")]
		public double RiskPerTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse.", Order = 14, GroupName = "02 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Kontrakte (fest)", Description = "Wird nur verwendet, wenn 'Festes Geldrisiko je Trade' auf False steht.", Order = 15, GroupName = "02 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Risiko (Ticks)", Description = "Ist der Abstand Einstieg->Stop kleiner als dieser Wert, wird das Signal verworfen. 0 = Filter aus.", Order = 16, GroupName = "02 Risiko")]
		public int MinRiskTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fenster in Swing einbeziehen", Description = "False (Standard): Das geformte Extrem umfasst nur die Kerzen NACH dem 5er-Fenster. True: auch die 5 Anfangskerzen zaehlen mit (deutlich weitere Stops, wenn die Eroeffnungskerze einen langen Docht hat).", Order = 20, GroupName = "03 Verhalten")]
		public bool IncludeWindowInSwing { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Nur erste Signalkerze", Description = "True: Ist die erste Signalkerze ungueltig, kein Trade an diesem Tag. False (Standard): naechste Signalkerze abwarten.", Order = 21, GroupName = "03 Verhalten")]
		public bool StrictFirstSignal { get; set; }
		#endregion
	}
}
