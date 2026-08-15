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
// TimedLong  —  Referenzstrategie: jeden Tag zur festen Uhrzeit long
//               NinjaTrader 8.1
//
// Bewusst ohne jedes Signal. Der Zweck ist ein BENCHMARK: Wenn eine aufwendige
// Strategie diese Baseline nicht schlaegt, misst sie keinen Edge, sondern nur die
// Grunddrift des Marktes im gewaehlten Zeitfenster.
//
// Regelwerk:
//  1. Einstieg bei der ersten Kerze, die um EntryHour:EntryMinute (Standard 16:00)
//     oder danach schliesst. Market-Order -> Fill zum Open der Folgekerze.
//     Richtung ueber DirectionLong: true = LONG (Standard), false = SHORT.
//     Bei Short werden Stop und Ziel gespiegelt.
//  2. Ausstieg bei der ersten Kerze, die um ExitHour:ExitMinute (Standard 22:00)
//     oder danach schliesst.
//  3. Genau ein Trade pro Handelstag.
//  4. Optionale Stop/Ziel-Klammer (UseStopTarget, Standard AN):
//        Stop = Einstieg - StopTicks x TickSize
//        Ziel = Einstieg + RewardMultiple x StopTicks x TickSize
//     Ohne Klammer laeuft die Position bis zum Zeit-Ausstieg — das ist die reine
//     Drift-Messung und der ehrlichere Benchmark.
//  5. Positionsgroesse:
//        Klammer aktiv + UseFixedRisk: abrunden( RiskAmount / (Stopdistanz x PointValue) )
//        sonst: feste Kontraktzahl aus Contracts
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class TimedLong : Strategy
	{
		private const string SignalLong  = "TL_Long";
		private const string SignalShort = "TL_Short";

		private DateTime currentDay = DateTime.MinValue;
		private bool     enteredToday;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Referenzstrategie ohne Signal: jeden Handelstag zur festen Uhrzeit long, Ausstieg zur festen Uhrzeit. Dient als Benchmark, gegen den sich die uebrigen Strategien messen lassen muessen.";
				Name                            = "TimedLong";
				Calculate                       = Calculate.OnBarClose;
				EntriesPerDirection             = 1;
				EntryHandling                   = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy    = true;   // Sicherheitsnetz zum Sessionende
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
				BarsRequiredToTrade             = 1;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Parameter-Defaults
				EntryHour      = 16;
				EntryMinute    = 0;
				ExitHour       = 22;
				ExitMinute     = 0;
				TradeMonday    = true;
				TradeTuesday   = true;
				TradeWednesday = true;
				TradeThursday  = true;
				TradeFriday    = true;
				DirectionLong  = true;    // false = short statt long
				UseStopTarget  = true;    // Klammer aktiv, damit RiskAmount/RewardMultiple greifen
				StopTicks      = 50;
				RewardMultiple = 1;
				UseFixedRisk   = true;
				RiskAmount     = 100;
				MaxContracts   = 50;
				Contracts      = 1;
				EnableDebugLog = false;
			}
			else if (State == State.DataLoaded)
			{
				// Ohne Klammer sind RiskAmount, RewardMultiple und UseFixedRisk wirkungslos,
				// stehen aber trotzdem im Parametergitter. Das einmal klar in den Log
				// schreiben, damit niemand ein Risikolimit annimmt, das es nicht gibt.
				if (!UseStopTarget)
					Log(Name + ": 'Stop/Ziel-Klammer aktiv' steht auf False — es gibt WEDER Stop NOCH Ziel. "
						+ "Die Position laeuft von " + EntryHour.ToString("00") + ":" + EntryMinute.ToString("00")
						+ " bis " + ExitHour.ToString("00") + ":" + ExitMinute.ToString("00")
						+ " durch, das Risiko je Trade ist unbegrenzt. 'Risiko je Trade' (" + RiskAmount
						+ ") und 'R-Ziel' (" + RewardMultiple + ") werden dabei IGNORIERT. "
						+ "Positionsgroesse ist fest: " + Contracts + " Kontrakt(e).", LogLevel.Warning);

				if (UseStopTarget && UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue des Instruments ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Die Positionsgroesse laesst sich damit nicht aus dem Geldrisiko berechnen, es wird auf "
						+ Contracts + " Kontrakt(e) zurueckgefallen. Point Value in Control Center -> Tools -> Instruments "
						+ "fuer " + Instrument.FullName + " pruefen (FDXS = 1).", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — Instrument=" + Instrument.FullName
						+ ", Einstieg " + EntryHour.ToString("00") + ":" + EntryMinute.ToString("00")
						+ ", Ausstieg " + ExitHour.ToString("00") + ":" + ExitMinute.ToString("00")
						+ ", Klammer=" + (UseStopTarget ? StopTicks + " Ticks / " + RewardMultiple + "R" : "aus"));
			}
		}

		// Ohne Stop gibt es keine Bezugsgroesse fuer ein Geldrisiko -> feste Kontraktzahl.
		private int CalcQuantity()
		{
			if (!UseStopTarget || !UseFixedRisk)
				return Contracts;

			double pointValue = Instrument.MasterInstrument.PointValue;
			if (pointValue <= 0 || StopTicks <= 0)
				return Contracts;

			// Risiko je Kontrakt = Stopdistanz in KURSEINHEITEN x Waehrung je Punkt
			double stopDistance   = StopTicks * TickSize;
			double riskPerContract = stopDistance * pointValue;
			if (riskPerContract <= 0)
				return Contracts;

			int qty = (int)Math.Floor(RiskAmount / riskPerContract);
			// Mindestens 1 Kontrakt: Ein uebersprungener Tag wuerde den Benchmark verzerren,
			// weil dann nicht mehr jeder Handelstag im Sample vertreten waere.
			return Math.Max(1, Math.Min(qty, MaxContracts));
		}

		private bool IsTradingDay(DayOfWeek d)
		{
			switch (d)
			{
				case DayOfWeek.Monday:    return TradeMonday;
				case DayOfWeek.Tuesday:   return TradeTuesday;
				case DayOfWeek.Wednesday: return TradeWednesday;
				case DayOfWeek.Thursday:  return TradeThursday;
				case DayOfWeek.Friday:    return TradeFriday;
				default:                  return false;      // Wochenende
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			// Neuer Handelstag -> Tagesflag zuruecksetzen
			if (Time[0].Date != currentDay)
			{
				currentDay   = Time[0].Date;
				enteredToday = false;
			}

			TimeSpan barClose  = Time[0].TimeOfDay;                        // NT8: Zeitstempel = Kerzenschluss
			TimeSpan entryTime = new TimeSpan(EntryHour, EntryMinute, 0);
			TimeSpan exitTime  = new TimeSpan(ExitHour,  ExitMinute,  0);

			// ---------- Ausstieg ----------
			if (barClose >= exitTime)
			{
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong("TimeExit", SignalLong);
				else if (Position.MarketPosition == MarketPosition.Short)
					ExitShort("TimeExit", SignalShort);
				else
					return;

				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " TL: Zeit-Ausstieg @ " + Close[0]);
				return;
			}

			// ---------- Einstieg ----------
			if (enteredToday)
				return;
			if (barClose < entryTime)
				return;
			if (!IsTradingDay(Time[0].DayOfWeek))
				return;
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			int    qty          = CalcQuantity();
			string signal       = DirectionLong ? SignalLong : SignalShort;
			double stopDistance = StopTicks * TickSize;
			double stopLevel    = Instrument.MasterInstrument.RoundToTickSize(
				DirectionLong ? Close[0] - stopDistance : Close[0] + stopDistance);
			double targetLevel  = Instrument.MasterInstrument.RoundToTickSize(
				DirectionLong ? Close[0] + RewardMultiple * stopDistance
				              : Close[0] - RewardMultiple * stopDistance);

			if (UseStopTarget)
			{
				SetStopLoss(signal, CalculationMode.Price, stopLevel, false);
				SetProfitTarget(signal, CalculationMode.Price, targetLevel);
			}

			if (DirectionLong)
				EnterLong(qty, SignalLong);
			else
				EnterShort(qty, SignalShort);

			enteredToday = true;

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " TL: " + (DirectionLong ? "LONG " : "SHORT ") + qty + " @ " + Close[0]
					+ (UseStopTarget
						? " | Stop=" + stopLevel + " | Ziel=" + targetLevel
						: " | ohne Klammer, Ausstieg per Zeit"));
		}

		// Stop und Ziel exakt auf den tatsaechlichen Fill nachrechnen.
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (!UseStopTarget)
				return;
			if (execution.Order == null)
				return;
			if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
				return;
			bool isLong = execution.Order.Name == SignalLong;
			if (!isLong && execution.Order.Name != SignalShort)
				return;

			double stopDistance = StopTicks * TickSize;
			string signal       = isLong ? SignalLong : SignalShort;

			SetStopLoss(signal, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(isLong ? price - stopDistance : price + stopDistance), false);
			SetProfitTarget(signal, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(isLong ? price + RewardMultiple * stopDistance
				                                                   : price - RewardMultiple * stopDistance));
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Einstieg – Stunde", Description = "Uhrzeit des taeglichen Einstiegs (lokale NT-Zeitzone)", Order = 1, GroupName = "01 Zeiten")]
		public int EntryHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Einstieg – Minute", Order = 2, GroupName = "01 Zeiten")]
		public int EntryMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Ausstieg – Stunde", Description = "Uhrzeit, zu der die Position geschlossen wird.", Order = 3, GroupName = "01 Zeiten")]
		public int ExitHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Ausstieg – Minute", Order = 4, GroupName = "01 Zeiten")]
		public int ExitMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Montag handeln", Order = 10, GroupName = "02 Wochentage")]
		public bool TradeMonday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dienstag handeln", Order = 11, GroupName = "02 Wochentage")]
		public bool TradeTuesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mittwoch handeln", Order = 12, GroupName = "02 Wochentage")]
		public bool TradeWednesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Donnerstag handeln", Order = 13, GroupName = "02 Wochentage")]
		public bool TradeThursday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Freitag handeln", Order = 14, GroupName = "02 Wochentage")]
		public bool TradeFriday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Richtung: Long", Description = "True (Standard): taeglicher Einstieg LONG. False: SHORT — Stop und Ziel werden dabei gespiegelt. Damit laesst sich dieselbe Uhrzeit in beide Richtungen testen.", Order = 15, GroupName = "02 Wochentage")]
		public bool DirectionLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop/Ziel-Klammer aktiv", Description = "False (Standard): kein Stop, kein Ziel — die Position laeuft bis zum Zeit-Ausstieg. Das ist die reine Drift-Messung. True: feste Klammer wie unten eingestellt.", Order = 20, GroupName = "03 Risiko")]
		public bool UseStopTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100000)]
		[Display(Name = "Stopdistanz (Ticks)", Description = "Abstand des Stops vom Einstieg. Nur wirksam bei aktiver Klammer. FDXS: 1 Tick = 1 Punkt.", Order = 21, GroupName = "03 Risiko")]
		public int StopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Ziel = Einstieg + Multiple x Stopdistanz. Nur wirksam bei aktiver Klammer.", Order = 22, GroupName = "03 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein Stopout etwa dem Betrag unten entspricht. Nur wirksam bei aktiver Klammer — ohne Stop gibt es keine Bezugsgroesse.", Order = 23, GroupName = "03 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den ein Stopout kostet. FDXS rechnet in EUR.", Order = 24, GroupName = "03 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse.", Order = 25, GroupName = "03 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Kontrakte (fest)", Description = "Positionsgroesse ohne Klammer bzw. wenn 'Groesse aus Geldrisiko' aus ist.", Order = 26, GroupName = "03 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jeden Einstieg und Ausstieg ins NinjaScript Output-Fenster.", Order = 30, GroupName = "04 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
