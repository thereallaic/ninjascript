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
//  3a. TAGES-EMA-FILTER (UseDailyEmaFilter, Standard an): Gehandelt wird nur, wenn der
//     Kurs auf der verlangten Seite des EMA im TAGESCHART liegt (DailyEmaAbove:
//     true = darueber, false = darunter). Dafuer wird eine Tages-Zusatzserie geladen.
//     Von Zusatzserien verarbeitet NinjaTrader nur ABGESCHLOSSENE Bars, der EMA
//     stuetzt sich also auf fertige Tage — die heutige Kerze fliesst nicht ein.
//  3b. FARBFILTER (UseColorFilter, Standard an): Einstieg nur, wenn unter den letzten
//     ColorLookback Kerzen (Standard 20, inkl. der gerade geschlossenen) STRIKT MEHR
//     als MinColorCount (Standard 10) in Handelsrichtung schlossen — bei Short also
//     rote (Close < Open), bei Long gruene. Dojis zaehlen fuer keine Seite.
//     Faellt der Filter durch, ist der Tag abgehakt; es wird nicht spaeter nachgerueckt.
//  4. Optionale Stop/Ziel-Klammer (UseStopTarget, Standard AN):
//        Stop = Einstieg - StopTicks x TickSize
//        Ziel = Einstieg + RewardMultiple x StopTicks x TickSize
//     Ohne Klammer laeuft die Position bis zum Zeit-Ausstieg — das ist die reine
//     Drift-Messung und der ehrlichere Benchmark.
//  4b. BREAK-EVEN-STOP (UseBreakEvenStop, Standard an): Erreicht der Buchgewinn
//     BreakEvenTriggerR (Standard 1R), wandert der Stop EINMALIG auf
//     BreakEvenOffsetR ab Einstieg (Standard 0 = Einstiegskurs). Er bewegt sich
//     nur in Gewinnrichtung und nur ein einziges Mal je Position.
//     Achtung: 0R deckt die Kommission NICHT — der Trade endet dann leicht negativ.
//  4c. ATR-SKALIERUNG (UseAtrStop, Standard AUS): Ist sie aktiv, gilt StopTicks als
//     MITTELWERT und wird mit dem Verhaeltnis aus aktueller Tages-ATR zu deren
//     langfristigem Schnitt multipliziert. Ruhige Phasen -> engerer Stop (das R-Ziel
//     rueckt in Reichweite), wilde Phasen -> weiterer Stop. Der Faktor ist auf
//     [1/AtrScaleLimit, AtrScaleLimit] gekappt. Weil die Positionsgroesse aus dem
//     Geldrisiko folgt, bleibt 1R dabei immer derselbe Geldbetrag.
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
		private EMA      dailyEma;      // EMA auf der Tages-Zusatzserie (BarsInProgress 1)
		private ATR      dailyAtr;          // ATR auf der Tages-Zusatzserie
		private SMA      dailyAtrAvg;       // langfristiger Mittelwert derselben ATR
		private double   activeStopDistance;// 1R dieser Position in Kurseinheiten
		private double   activeStopLevel;   // aktuell gesetzter Stop der laufenden Position
		private bool     beMoved;           // Break-even-Schritt fuer diese Position erledigt

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
				UseDailyEmaFilter = true;
				DailyEmaPeriod    = 50;
				DailyEmaAbove     = true;  // true = nur ueber dem Tages-EMA handeln
				UseColorFilter = true;
				ColorLookback  = 20;      // wie viele Kerzen vor dem Einstieg gezaehlt werden
				MinColorCount  = 10;      // STRIKT mehr als dieser Wert muessen passen
				UseBreakEvenStop  = true;
				BreakEvenTriggerR = 1.0;  // ab 1R Buchgewinn ...
				BreakEvenOffsetR  = 0.0;  // ... Stop auf den Einstieg (0 = reines Break-even)
				UseStopTarget  = true;    // Klammer aktiv, damit RiskAmount/RewardMultiple greifen
				StopTicks      = 50;
				UseAtrStop       = false;  // Standard aus: feste Stopdistanz wie bisher
				AtrPeriod        = 14;
				AtrAveragePeriod = 100;
				AtrScaleLimit    = 2.0;
				RewardMultiple = 1;
				UseFixedRisk   = true;
				RiskAmount     = 100;
				MaxContracts   = 50;
				Contracts      = 1;
				EnableDebugLog = false;
			}
			else if (State == State.Configure)
			{
				// Der Farbfilter braucht Vorlauf, sonst greift er am Anfang der Serie ins Leere
				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, UseColorFilter ? ColorLookback : 1);

				// Tages-Zusatzserie fuer den uebergeordneten Trendfilter.
				// NinjaTrader verarbeitet von Zusatzserien nur ABGESCHLOSSENE Bars — die
				// heutige Tageskerze ist um 14:31 also noch nicht dabei. Der EMA-Wert
				// stammt damit aus abgeschlossenen Tagen, kein Blick in die Zukunft.
				if (UseDailyEmaFilter || UseAtrStop)
					AddDataSeries(BarsPeriodType.Day, 1);
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

				if (UseDailyEmaFilter)
					dailyEma = EMA(Closes[1], DailyEmaPeriod);

				if (UseAtrStop)
				{
					dailyAtr    = ATR(BarsArray[1], AtrPeriod);
					dailyAtrAvg = SMA(dailyAtr, AtrAveragePeriod);
				}

				if (EnableDebugLog)
					Print(Name + ": Start — Instrument=" + Instrument.FullName
						+ ", Einstieg " + EntryHour.ToString("00") + ":" + EntryMinute.ToString("00")
						+ ", Ausstieg " + ExitHour.ToString("00") + ":" + ExitMinute.ToString("00")
						+ ", Klammer=" + (UseStopTarget ? StopTicks + " Ticks / " + RewardMultiple + "R" : "aus"));
			}
		}

		// Stopdistanz dieser Position in Kurseinheiten.
		//
		// UseAtrStop = false: schlicht StopTicks x TickSize.
		//
		// UseAtrStop = true: StopTicks bleibt der MITTELWERT und wird mit dem Verhaeltnis
		// aus aktueller Tages-ATR zu ihrem langfristigen Schnitt skaliert:
		//     Distanz = StopTicks x TickSize x (ATR / Ø ATR)
		// Bei durchschnittlicher Volatilitaet aendert sich also nichts, in ruhigen Phasen
		// wird der Stop enger (und das R-Ziel damit erreichbar), in wilden Phasen weiter.
		// Der Faktor ist auf [1/AtrScaleLimit, AtrScaleLimit] begrenzt, damit ein einzelner
		// Ausreisser keine absurden Positionsgroessen erzeugt.
		private double CurrentStopDistance()
		{
			double baseDist = StopTicks * TickSize;
			if (!UseAtrStop || dailyAtr == null || dailyAtrAvg == null)
				return baseDist;
			if (CurrentBars.Length < 2 || CurrentBars[1] < AtrPeriod + AtrAveragePeriod)
				return baseDist;                            // noch kein verlaesslicher Schnitt

			double atr = dailyAtr[0], avg = dailyAtrAvg[0];
			if (atr <= 0 || avg <= 0)
				return baseDist;

			double f = Math.Max(1.0 / AtrScaleLimit, Math.Min(AtrScaleLimit, atr / avg));
			double d = Instrument.MasterInstrument.RoundToTickSize(baseDist * f);
			return Math.Max(TickSize, d);
		}

		// Ohne Stop gibt es keine Bezugsgroesse fuer ein Geldrisiko -> feste Kontraktzahl.
		private int CalcQuantity(double stopDistance)
		{
			if (!UseStopTarget || !UseFixedRisk)
				return Contracts;

			double pointValue = Instrument.MasterInstrument.PointValue;
			if (pointValue <= 0 || stopDistance <= 0)
				return Contracts;

			// Risiko je Kontrakt = Stopdistanz in KURSEINHEITEN x Waehrung je Punkt
			double riskPerContract = stopDistance * pointValue;
			if (riskPerContract <= 0)
				return Contracts;

			int qty = (int)Math.Floor(RiskAmount / riskPerContract);
			// Mindestens 1 Kontrakt: Ein uebersprungener Tag wuerde den Benchmark verzerren,
			// weil dann nicht mehr jeder Handelstag im Sample vertreten waere.
			return Math.Max(1, Math.Min(qty, MaxContracts));
		}

		// Zaehlt unter den letzten ColorLookback Kerzen (inkl. der gerade geschlossenen)
		// diejenigen in Handelsrichtung: bei Short rote (Close < Open), bei Long gruene.
		// Dojis (Close == Open) zaehlen fuer keine Seite und wirken damit leicht bremsend.
		// Bestanden ist der Filter erst, wenn es STRIKT mehr als MinColorCount sind.
		private int CountDirectionalCandles()
		{
			int count = 0;
			for (int i = 0; i < ColorLookback; i++)
			{
				if (DirectionLong) { if (Close[i] > Open[i]) count++; }
				else               { if (Close[i] < Open[i]) count++; }
			}
			return count;
		}

		// Einmaliger Break-even-Schritt: Erreicht der Buchgewinn BreakEvenTriggerR,
		// wandert der Stop auf BreakEvenOffsetR (in R ab Einstieg, 0 = Einstiegskurs).
		//
		// Bewertet wird das High/Low der abgeschlossenen 1-Min-Kerze, verschoben wird erst
		// nach deren Schluss. Ein echter Break-even-Stop reagierte im Moment der Beruehrung
		// — die Schaetzung faellt hier also eher zu VORSICHTIG aus, nicht zu guenstig.
		private void ManageBreakEven()
		{
			if (!UseBreakEvenStop || !UseStopTarget)
				return;                                    // ohne Klammer gibt es keinen Stop

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				beMoved = false;                           // Zustand fuer die naechste Position
				return;
			}

			if (beMoved)
				return;                                    // es gibt nur einen Schritt

			bool   isLong = Position.MarketPosition == MarketPosition.Long;
			double entry  = Position.AveragePrice;
			double dist   = activeStopDistance > 0 ? activeStopDistance : StopTicks * TickSize;  // 1R
			if (dist <= 0)
				return;

			double mfeR = (isLong ? High[0] - entry : entry - Low[0]) / dist;
			if (mfeR < BreakEvenTriggerR)
				return;

			double newStop = isLong ? entry + BreakEvenOffsetR * dist
			                        : entry - BreakEvenOffsetR * dist;

			// Der Stop darf nicht auf oder jenseits des aktuellen Kurses liegen, sonst
			// loest NinjaTrader ihn sofort aus. Passiert, wenn der Kurs innerhalb der
			// Kerze vorlief und wieder zurueckkam.
			bool   clamped = false;
			double limit   = isLong ? Close[0] - TickSize : Close[0] + TickSize;
			if (isLong && newStop > limit)  { newStop = limit; clamped = true; }
			if (!isLong && newStop < limit) { newStop = limit; clamped = true; }
			newStop = Instrument.MasterInstrument.RoundToTickSize(newStop);

			// Nur in Gewinnrichtung verschieben, niemals zurueck
			bool improves = isLong ? newStop > activeStopLevel : newStop < activeStopLevel;
			if (!improves)
				return;

			activeStopLevel = newStop;
			beMoved         = true;
			SetStopLoss(isLong ? SignalLong : SignalShort, CalculationMode.Price, newStop, false);

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " TL: Break-even — Stop von "
					+ Math.Round(isLong ? entry - dist : entry + dist, 2) + " auf " + newStop
					+ " (MFE " + Math.Round(mfeR, 2) + "R)" + (clamped ? " | auf Marktnaehe begrenzt" : ""));
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

			// Positionsverwaltung zuerst — laeuft unabhaengig von Fenster und Filtern
			ManageBreakEven();

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

			// ---------- Uebergeordneter Trendfilter: Tages-EMA ----------
			if (UseDailyEmaFilter)
			{
				if (CurrentBars[1] < DailyEmaPeriod)
					return;                                  // noch nicht genug Tageskerzen

				double lvl   = dailyEma[0];                  // EMA per letzter abgeschlossener Tageskerze
				bool   above = Close[0] > lvl;

				if (above != DailyEmaAbove)
				{
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " TL: kein Trade — Tages-EMA" + DailyEmaPeriod
							+ ": Kurs " + Close[0] + (above ? " UEBER " : " UNTER ") + Math.Round(lvl, 2)
							+ ", verlangt ist " + (DailyEmaAbove ? "darueber" : "darunter"));
					enteredToday = true;
					return;
				}
			}

			// ---------- Farbfilter: Momentum der letzten Kerzen muss zur Richtung passen ----------
			if (UseColorFilter)
			{
				if (CurrentBar < ColorLookback)
					return;

				int matching = CountDirectionalCandles();
				if (matching <= MinColorCount)
				{
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " TL: kein Trade — Farbfilter: nur "
							+ matching + " von " + ColorLookback + " Kerzen "
							+ (DirectionLong ? "gruen" : "rot") + " (noetig: mehr als " + MinColorCount + ")");
					enteredToday = true;   // Tag abhaken, kein spaeteres Nachruecken
					return;
				}
			}

			double stopDistance = CurrentStopDistance();
			int    qty          = CalcQuantity(stopDistance);
			string signal       = DirectionLong ? SignalLong : SignalShort;
			activeStopDistance  = stopDistance;   // fuer Fill-Nachrechnung und Break-even
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

			double stopDistance = activeStopDistance > 0 ? activeStopDistance : CurrentStopDistance();
			string signal       = isLong ? SignalLong : SignalShort;

			SetStopLoss(signal, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(isLong ? price - stopDistance : price + stopDistance), false);
			SetProfitTarget(signal, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(isLong ? price + RewardMultiple * stopDistance
				                                                   : price - RewardMultiple * stopDistance));

			// Ausgangslage fuer den Break-even-Schritt
			activeStopLevel = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price - stopDistance : price + stopDistance);
			beMoved = false;
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
		[Display(Name = "Tages-EMA-Filter aktiv", Description = "True (Standard): Es wird nur gehandelt, wenn der Kurs auf der richtigen Seite des EMA im TAGESCHART liegt. Fuegt eine Tages-Datenserie hinzu.", Order = 13, GroupName = "03 Signalfilter")]
		public bool UseDailyEmaFilter { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Tages-EMA Periode", Description = "Periode des EMA auf Tagesbasis. Standard 50.", Order = 14, GroupName = "03 Signalfilter")]
		public int DailyEmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Nur UEBER dem Tages-EMA", Description = "True (Standard): handeln nur, wenn der Kurs ueber dem Tages-EMA liegt. False: nur darunter — sinnvoll fuer einen Short-Lauf im Abwaertstrend.", Order = 15, GroupName = "03 Signalfilter")]
		public bool DailyEmaAbove { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Farbfilter aktiv", Description = "True (Standard): Einstieg nur, wenn genug der letzten Kerzen in Handelsrichtung schlossen. Bei Short zaehlen rote Kerzen, bei Long gruene.", Order = 16, GroupName = "03 Signalfilter")]
		public bool UseColorFilter { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Kerzen-Rueckblick", Description = "Wie viele Kerzen vor dem Einstieg gezaehlt werden, inklusive der gerade geschlossenen. Standard 20.", Order = 17, GroupName = "03 Signalfilter")]
		public int ColorLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindestanzahl passender Kerzen", Description = "Es muessen STRIKT MEHR als so viele Kerzen in Handelsrichtung geschlossen haben. Standard 10 bei 20 Kerzen Rueckblick, also mindestens 11.", Order = 18, GroupName = "03 Signalfilter")]
		public int MinColorCount { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop/Ziel-Klammer aktiv", Description = "False (Standard): kein Stop, kein Ziel — die Position laeuft bis zum Zeit-Ausstieg. Das ist die reine Drift-Messung. True: feste Klammer wie unten eingestellt.", Order = 20, GroupName = "04 Risiko")]
		public bool UseStopTarget { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100000)]
		[Display(Name = "Stopdistanz (Ticks)", Description = "Abstand des Stops vom Einstieg. Nur wirksam bei aktiver Klammer. FDXS: 1 Tick = 1 Punkt.", Order = 21, GroupName = "04 Risiko")]
		public int StopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stopdistanz an ATR skalieren", Description = "False (Standard): feste Stopdistanz aus 'Stopdistanz (Ticks)'. True: dieser Wert gilt als MITTELWERT und wird mit ATR/Ø-ATR der Tageskerzen skaliert — in ruhigen Phasen enger, in wilden weiter.", Order = 30, GroupName = "04 Risiko")]
		public bool UseAtrStop { get; set; }

		[NinjaScriptProperty]
		[Range(2, 200)]
		[Display(Name = "ATR-Periode (Tage)", Description = "Periode der ATR auf Tagesbasis. Standard 14.", Order = 31, GroupName = "04 Risiko")]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(10, 1000)]
		[Display(Name = "ATR-Referenzschnitt (Tage)", Description = "Ueber wie viele Tage der Vergleichs-Mittelwert der ATR gebildet wird. Bestimmt, was als 'normale' Volatilitaet gilt. Standard 100.", Order = 32, GroupName = "04 Risiko")]
		public int AtrAveragePeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1.1, 10)]
		[Display(Name = "ATR-Skalierungsgrenze", Description = "Der Faktor ATR/Ø-ATR wird auf [1/Grenze, Grenze] gekappt. Standard 2,0: die Stopdistanz bleibt zwischen der Haelfte und dem Doppelten des eingestellten Werts.", Order = 33, GroupName = "04 Risiko")]
		public double AtrScaleLimit { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Ziel = Einstieg + Multiple x Stopdistanz. Nur wirksam bei aktiver Klammer.", Order = 22, GroupName = "04 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein Stopout etwa dem Betrag unten entspricht. Nur wirksam bei aktiver Klammer — ohne Stop gibt es keine Bezugsgroesse.", Order = 23, GroupName = "04 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den ein Stopout kostet. FDXS rechnet in EUR.", Order = 24, GroupName = "04 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse.", Order = 25, GroupName = "04 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Break-even-Stop aktiv", Description = "True: Erreicht der Buchgewinn den Ausloeser, wandert der Stop einmalig auf den Einstieg. Braucht eine aktive Stop/Ziel-Klammer.", Order = 27, GroupName = "04 Risiko")]
		public bool UseBreakEvenStop { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 20)]
		[Display(Name = "Break-even Ausloeser (R)", Description = "Ab welchem Buchgewinn in R der Stop nachgezogen wird. Standard 1,0.", Order = 28, GroupName = "04 Risiko")]
		public double BreakEvenTriggerR { get; set; }

		[NinjaScriptProperty]
		[Range(-2, 20)]
		[Display(Name = "Break-even Ziel (R ab Einstieg)", Description = "Wohin der Stop springt. 0 = exakt Einstieg (Standard), deckt die Kommission NICHT. Bei 8 Kontrakten und 15,20 $ Gebuehren waeren rund 0,16 R kostenneutral.", Order = 29, GroupName = "04 Risiko")]
		public double BreakEvenOffsetR { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Kontrakte (fest)", Description = "Positionsgroesse ohne Klammer bzw. wenn 'Groesse aus Geldrisiko' aus ist.", Order = 26, GroupName = "04 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jeden Einstieg und Ausstieg ins NinjaScript Output-Fenster.", Order = 30, GroupName = "05 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
