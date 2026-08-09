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
// OpeningPullbackSwingReverse1R  —  Variante 4, NinjaTrader 8.1
//
// Die UMKEHRUNG von OpeningPullbackSwing1R. Alles bleibt gleich — Richtungsentscheid,
// Signalkerze, Zeitpunkt, Positionsgroesse — nur die Orderrichtung wird gedreht:
//
//     Long-Bias  -> wo Variante 3 LONG  ging, gehen wir SHORT
//     Short-Bias -> wo Variante 3 SHORT ging, gehen wir LONG
//
// Regelwerk:
//  1. Referenzkurs = Open der 1-Minuten-Kerze um 09:00.
//  2. Die ersten 5 Kerzen (09:00–09:05) werden nur beobachtet.
//     Schlusskurs der 5. Kerze > Referenz  -> Long-Bias  (= wir gehen SHORT)
//     Schlusskurs der 5. Kerze < Referenz  -> Short-Bias (= wir gehen LONG)
//  3. Ausloeser UNVERAENDERT: bei Long-Bias die erste GRUENE Kerze nach dem Fenster,
//     bei Short-Bias die erste ROTE Kerze. Gleicher Zeitpunkt wie in Variante 3.
//  4. Stop/Ziel — zwei Modi, siehe Parameter "Struktureller Stop":
//
//     a) Spiegel-Modus (Standard, UseStructuralStop = false)
//        R ist exakt das R, das Variante 3 verwendet haette; Stop und Ziel werden nur
//        um den Einstieg gespiegelt. Damit ist jeder Trade das fotografische Negativ:
//        Was in Variante 3 ins Ziel lief, laeuft hier in den Stop und umgekehrt.
//        Beispiel Long-Bias:  R = Close − (SwingLow − Offset)
//                             Stop = Close + R      Ziel = Close − RewardMultiple * R
//
//     b) Struktureller Modus (UseStructuralStop = true)
//        Der Stop sitzt am Swing-Extrem der Gegenseite — bei einem Short also ueber dem
//        Swing-High. Handelstechnisch sinnvoller, aber KEIN sauberer Spiegel mehr,
//        weil R (und damit die Positionsgroesse) von Variante 3 abweicht.
//
//  5. Maximal 1 Trade pro Tag. Kein Einstieg mehr nach der Cutoff-Zeit.
//
// Wichtig: 1-Minuten-Datenserie verwenden. Zeitzone Berlin in NT einstellen
// (Tools > Options > General > Time zone).
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class OpeningPullbackSwingReverse1R : Strategy
	{
		private const string SignalLong  = "OPSR1R_Long";
		private const string SignalShort = "OPSR1R_Short";

		private DateTime currentDay = DateTime.MinValue;
		private double   openPrice;          // Open der 09:00-Kerze
		private int      windowBarCount;     // Anzahl verarbeiteter Anfangskerzen
		private double   lastWindowClose;    // Close der letzten Kerze im Fenster
		private double   swingLow;           // tiefstes Low des geformten Pullbacks
		private double   swingHigh;          // hoechstes High des geformten Pullbacks
		private int      bias;               // +1 Long-Bias, -1 Short-Bias, 0 kein Trade
		private bool     biasDecided;
		private bool     entryDone;          // heute wurde eingestiegen bzw. der Tag ist abgehakt
		private double   stopPrice;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Umkehrung von OpeningPullbackSwing1R: identischer Richtungsentscheid, identische Signalkerze, identischer Zeitpunkt — aber gedrehte Orderrichtung. Wo Variante 3 long ging, wird hier geshortet.";
				Name                            = "OpeningPullbackSwingReverse1R";
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
				RewardMultiple        = 1;
				StopOffsetTicks       = 2;
				UseStructuralStop     = false;  // Standard: sauberer Spiegel von Variante 3
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
			swingLow  = Math.Min(swingLow,  Low[0]);
			swingHigh = Math.Max(swingHigh, High[0]);

			// ---------- Phase 4: Einstieg — gleicher Ausloeser, GEDREHTE Richtung ----------

			// Long-Bias + gruene Signalkerze: Variante 3 ginge long -> wir gehen SHORT
			if (bias == 1 && isGreen)
			{
				double risk;
				if (UseStructuralStop)
				{
					stopPrice = Instrument.MasterInstrument.RoundToTickSize(swingHigh + StopOffsetTicks * TickSize);
					risk      = stopPrice - Close[0];
				}
				else
				{
					// Spiegel: exakt das R von Variante 3, nur auf die andere Seite geklappt
					risk      = Close[0] - Instrument.MasterInstrument.RoundToTickSize(swingLow - StopOffsetTicks * TickSize);
					stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + risk);
				}

				if (risk < Math.Max(1, MinRiskTicks) * TickSize)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				int qty = CalcQuantity(risk);
				if (qty < 1) // R zu gross fuer das Geldrisiko -> Trade auslassen
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				SetStopLoss(SignalShort, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalShort, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] - RewardMultiple * risk));
				EnterShort(qty, SignalShort);
				entryDone = true;
				return;
			}

			// Short-Bias + rote Signalkerze: Variante 3 ginge short -> wir gehen LONG
			if (bias == -1 && isRed)
			{
				double risk;
				if (UseStructuralStop)
				{
					stopPrice = Instrument.MasterInstrument.RoundToTickSize(swingLow - StopOffsetTicks * TickSize);
					risk      = Close[0] - stopPrice;
				}
				else
				{
					risk      = Instrument.MasterInstrument.RoundToTickSize(swingHigh + StopOffsetTicks * TickSize) - Close[0];
					stopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - risk);
				}

				if (risk < Math.Max(1, MinRiskTicks) * TickSize)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				int qty = CalcQuantity(risk);
				if (qty < 1)
				{
					if (StrictFirstSignal) entryDone = true;
					return;
				}

				SetStopLoss(SignalLong, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalLong, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * risk));
				EnterLong(qty, SignalLong);
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
		[Display(Name = "Reward-Multiple (R)", Description = "Take-Profit = Einstieg +/- Multiple * R (Standard 1)", Order = 10, GroupName = "02 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop-Offset (Ticks)", Description = "Puffer am Swing-Extrem (FDXS: 1 Tick = 1 Punkt).", Order = 11, GroupName = "02 Risiko")]
		public int StopOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Struktureller Stop", Description = "False (Standard): R wird von Variante 3 uebernommen und nur gespiegelt — sauberer Umkehrtest. True: Stop sitzt am Swing-Extrem der Gegenseite; handelstechnisch sinnvoller, aber kein exakter Spiegel mehr.", Order = 12, GroupName = "02 Risiko")]
		public bool UseStructuralStop { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Festes Geldrisiko je Trade", Description = "True (Standard): Kontraktzahl wird so berechnet, dass 1R dem Betrag unten entspricht. False: feste Kontraktzahl.", Order = 13, GroupName = "02 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade (1R)", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den 1R kosten darf. FDXS rechnet in EUR -> 100 = 100 EUR.", Order = 14, GroupName = "02 Risiko")]
		public double RiskPerTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse.", Order = 15, GroupName = "02 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Kontrakte (fest)", Description = "Wird nur verwendet, wenn 'Festes Geldrisiko je Trade' auf False steht.", Order = 16, GroupName = "02 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Risiko (Ticks)", Description = "Ist der Abstand Einstieg->Stop kleiner als dieser Wert, wird das Signal verworfen. 0 = Filter aus.", Order = 17, GroupName = "02 Risiko")]
		public int MinRiskTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fenster in Swing einbeziehen", Description = "False (Standard): Das geformte Extrem umfasst nur die Kerzen NACH dem 5er-Fenster. True: auch die 5 Anfangskerzen zaehlen mit.", Order = 20, GroupName = "03 Verhalten")]
		public bool IncludeWindowInSwing { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Nur erste Signalkerze", Description = "True: Ist die erste Signalkerze ungueltig, kein Trade an diesem Tag. False (Standard): naechste Signalkerze abwarten.", Order = 21, GroupName = "03 Verhalten")]
		public bool StrictFirstSignal { get; set; }
		#endregion
	}
}
