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
// OpeningPullback2R  —  Opening-Strategie fuer FDXS (Micro-DAX), NinjaTrader 8.1
//
// Regelwerk:
//  1. Referenzkurs = Open der 1-Minuten-Kerze um 09:00.
//  2. Die ersten 5 Kerzen (09:00–09:05) werden nur beobachtet.
//     Schlusskurs der 5. Kerze > Referenz  -> Long-Bias
//     Schlusskurs der 5. Kerze < Referenz  -> Short-Bias
//  3. Long:  Einstieg per Market beim Schluss der ERSTEN GRUENEN Kerze nach dem Fenster.
//     Short: Einstieg per Market beim Schluss der ERSTEN ROTEN  Kerze nach dem Fenster.
//  4. Stop (Long)  = Close der ZULETZT gesehenen ROTEN   Kerze vor dem Einstieg − Offset.
//     Stop (Short) = Close der ZULETZT gesehenen GRUENEN Kerze vor dem Einstieg + Offset.
//     "Zuletzt gesehen" wird ab 09:00 fortlaufend mitgefuehrt, deckt also sowohl die
//     5 Anfangskerzen als auch die Wartekerzen danach ab.
//     R = Distanz Einstieg->Stop.  Take-Profit = Einstieg +/- RewardMultiple * R.
//  5. Maximal 1 Trade pro Tag. Kein Einstieg mehr nach der Cutoff-Zeit.
//
// Wichtig: 1-Minuten-Datenserie verwenden. Zeitstempel = Schlusszeit der Kerze
// (NT8-Standard). Der PC / NinjaTrader muss auf Zeitzone Berlin stehen, damit
// "09:00" auch die Eroeffnung trifft (Tools > Options > General > Time zone).
//
// Fill-Timing: Bei Calculate.OnBarClose wird die Market-Order beim Schluss der
// Signalkerze abgeschickt und zum Open der Folgekerze gefuellt. Das ist korrekt und
// realistisch — ein Fill exakt zum Schlusskurs der Signalkerze waere nicht handelbar.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class OpeningPullback2R : Strategy
	{
		private const string SignalLong  = "OP2R_Long";
		private const string SignalShort = "OP2R_Short";

		private DateTime currentDay = DateTime.MinValue;
		private double   openPrice;          // Open der 09:00-Kerze
		private int      windowBarCount;     // Anzahl verarbeiteter Anfangskerzen
		private double   lastWindowClose;    // Close der letzten Kerze im Fenster
		private double   lastRedClose;       // Close der zuletzt gesehenen ROTEN Kerze (ab 09:00)
		private double   lastGreenClose;     // Close der zuletzt gesehenen GRUENEN Kerze (ab 09:00)
		private double   lowestRedClose;     // optionale Alt-Basis: tiefster roter Close im Fenster
		private double   highestGreenClose;  // optionale Alt-Basis: hoechster gruener Close im Fenster
		private double   lowestCloseAll;     // Fallback: tiefster Close aller Fensterkerzen
		private double   highestCloseAll;    // Fallback: hoechster Close aller Fensterkerzen
		private int      bias;               // +1 Long, -1 Short, 0 kein Trade
		private bool     biasDecided;
		private bool     entryDone;          // heute wurde eingestiegen bzw. der Tag ist abgehakt
		private double   stopPrice;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Opening-Strategie: Richtung aus den ersten 5 1-Min-Kerzen vs. 9:00-Open. Long-Einstieg auf der ersten gruenen Kerze danach, Stop auf dem Close der letzten roten Kerze, Ziel = 2R. Short spiegelbildlich.";
				Name                            = "OpeningPullback2R";
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
				RewardMultiple        = 2;
				StopOffsetTicks       = 2;
				UseWindowExtremeStop  = false;
				MinRiskTicks          = 0;
				AllowFallbackStop     = true;
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
			currentDay        = day;
			openPrice         = 0;
			windowBarCount    = 0;
			lastWindowClose   = 0;
			lastRedClose      = double.MaxValue;
			lastGreenClose    = double.MinValue;
			lowestRedClose    = double.MaxValue;
			highestGreenClose = double.MinValue;
			lowestCloseAll    = double.MaxValue;
			highestCloseAll   = double.MinValue;
			bias              = 0;
			biasDecided       = false;
			entryDone         = false;
			stopPrice         = 0;
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

				if (isRed)
				{
					lastRedClose   = Close[0];
					lowestRedClose = Math.Min(lowestRedClose, Close[0]);
				}
				if (isGreen)
				{
					lastGreenClose    = Close[0];
					highestGreenClose = Math.Max(highestGreenClose, Close[0]);
				}

				lowestCloseAll  = Math.Min(lowestCloseAll, Close[0]);
				highestCloseAll = Math.Max(highestCloseAll, Close[0]);
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

			// ---------- Phase 3: Einstieg auf der ersten Signalkerze ----------
			if (bias == 1 && isGreen)
			{
				double basis = UseWindowExtremeStop ? lowestRedClose : lastRedClose;
				if (basis == double.MaxValue)
					basis = AllowFallbackStop ? lowestCloseAll : double.MaxValue;
				if (basis == double.MaxValue) { entryDone = true; return; } // keine Stop-Basis vorhanden

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis - StopOffsetTicks * TickSize);
				double risk = Close[0] - stopPrice;

				// Zu klein (Signalkerze auf/unter Stop-Niveau) oder unter dem Mindest-R -> ungueltig
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
				double basis = UseWindowExtremeStop ? highestGreenClose : lastGreenClose;
				if (basis == double.MinValue)
					basis = AllowFallbackStop ? highestCloseAll : double.MinValue;
				if (basis == double.MinValue) { entryDone = true; return; }

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis + StopOffsetTicks * TickSize);
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

			// Keine Signalkerze -> Struktur fuer den Stop fortschreiben
			if (isRed)   lastRedClose   = Close[0];
			if (isGreen) lastGreenClose = Close[0];
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
		[Range(0.5, 10)]
		[Display(Name = "Reward-Multiple (R)", Description = "Take-Profit = Einstieg +/- Multiple * R (Standard 2)", Order = 10, GroupName = "02 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop-Offset (Ticks)", Description = "Puffer unter/ueber der Stop-Basis. 0 = exakt auf dem Close der letzten Gegenkerze (FDXS: 1 Tick = 1 Punkt).", Order = 11, GroupName = "02 Risiko")]
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
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse (Schutz vor Mini-Stops).", Order = 14, GroupName = "02 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Kontrakte (fest)", Description = "Wird nur verwendet, wenn 'Festes Geldrisiko je Trade' auf False steht.", Order = 15, GroupName = "02 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop aus Fenster-Extrem", Description = "False (Standard): Stop = Close der zuletzt gesehenen Gegenkerze. True: Stop = tiefster roter / hoechster gruener Close der 5 Anfangskerzen (altes Verhalten, deutlich weitere Stops).", Order = 16, GroupName = "02 Risiko")]
		public bool UseWindowExtremeStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Risiko (Ticks)", Description = "Ist der Abstand Einstieg->Stop kleiner als dieser Wert, wird das Signal verworfen (Stop laege im Rauschen). 0 = Filter aus.", Order = 17, GroupName = "02 Risiko")]
		public int MinRiskTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fallback-Stop erlauben", Description = "Keine Gegenkerze seit 09:00 vorhanden: tiefsten/hoechsten Close aller Fensterkerzen als Stop-Basis nutzen. Sonst kein Trade.", Order = 20, GroupName = "03 Verhalten")]
		public bool AllowFallbackStop { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Nur erste Signalkerze", Description = "True: Ist die erste Signalkerze ungueltig (Close jenseits des Stops), kein Trade an diesem Tag. False (Standard): naechste Signalkerze abwarten.", Order = 21, GroupName = "03 Verhalten")]
		public bool StrictFirstSignal { get; set; }
		#endregion
	}
}
