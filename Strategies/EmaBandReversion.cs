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
// EmaBandReversion  —  Rueckkehr in die EMA50-Flaeche mit ATR-Stop und R-Leiter-Trailing
//                      NinjaTrader 8.1, ausgelegt auf 1-Minuten-Kerzen
//
// DIE FLAECHE: EMA(50) +/- BandStdDevMultiple (Standard 2) x Standardabweichung
// (Standard ueber 50 Kerzen). Oberkante = EMA + 2*SD, Unterkante = EMA - 2*SD.
//
// EINSTIEG (Long, Short spiegelbildlich):
//  1. Der Kurs muss zuerst OBERHALB der Flaeche stehen (Kerzenschluss ueber der
//     Oberkante) — das schaltet die Long-Seite scharf.
//  2. Kommt danach eine Kerze IN die Flaeche (Standard: Low beruehrt die Oberkante;
//     per Schalter alternativ: Kerze muss IN der Flaeche schliessen), wird zur
//     Eroeffnung der Folgekerze Long eingestiegen.
//  3. Faellt die Signalkerze komplett DURCH die Flaeche (Schluss unter der
//     Unterkante), ist das kein Ruecksetzer mehr, sondern ein Durchbruch — kein Trade.
//  4. Nach dem Trade ist die Seite erst wieder scharf, wenn der Kurs erneut oberhalb
//     der Flaeche geschlossen hat (verhindert Wiedereinstiegs-Dauerfeuer in der Flaeche).
//
// RISIKO & TRAILING (R-Leiter):
//  - 1R = aktueller ATR-Wert (Periode einstellbar). Stop = Einstieg - 1R,
//    Ziel = Einstieg + InitialTargetR x R (Standard 2R).
//  - Erreicht der SCHLUSSKURS Einstieg + 1R, wird Stop um 1R nachgezogen (auf
//    Einstieg) und das Ziel um 1R weiter (auf 3R). Bei +2R: Stop auf +1R, Ziel auf
//    4R usw. — immer weiter, bis eine Kerze Stop ODER Ziel trifft, bevor die
//    naechste Stufe erreicht ist. Eine grosse Kerze kann mehrere Stufen auf einmal
//    schalten.
//  - Bewusst OnBarClose (Stufen schalten nur am Kerzenschluss) — die Tick-Variante
//    fuer den Livebetrieb (Calculate.OnEachTick) ruesten wir nach, wenn das
//    Regelwerk auf Kerzenbasis validiert ist. Stop und Ziel liegen aber als echte
//    Orders im Markt und fuellen auch INTRABAR.
//
// FILTER (alle standardmaessig AUS, zum schrittweisen Zuschalten beim Testen):
//  - Zeitfenster, Max. Trades pro Tag (0 = unbegrenzt), Vortagesrange-Filter
//    (Long nur ueber dem Vortageshoch, Short nur unter dem Vortagestief).
//  - Weitere Einstiegs-Indikatoren kommen spaeter in EntryFiltersOk() dazu.
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class EmaBandReversion : Strategy
	{
		private const string SignalLong  = "EBR_Long";
		private const string SignalShort = "EBR_Short";

		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;

		private EMA    bandEma;
		private StdDev bandSd;
		private ATR    atr;

		private bool   longArmed;        // Kurs schloss zuletzt OBERHALB der Flaeche
		private bool   shortArmed;       // Kurs schloss zuletzt UNTERHALB der Flaeche

		// Laufender Trade (R-Leiter)
		private double activeEntryPrice;
		private double activeR;          // 1R in Punkten (ATR bei Signal)
		private int    trailStep;        // 0 = Ausgangszustand, 1 = erste Stufe usw.
		private double pendingR;         // ATR der Signalkerze, bis der Fill da ist

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Rueckkehr in die EMA50-Flaeche (EMA +/- 2 Standardabweichungen) auf 1-Minuten-Basis. Stop = 1 ATR, Ziel 2R, ab +1R R-Leiter-Trailing: Stop und Ziel wandern je 1R mit.";
				Name                            = "EmaBandReversion";
				Calculate                       = Calculate.OnBarClose;
				EntriesPerDirection             = 1;
				EntryHandling                   = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy    = true;
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
				BarsRequiredToTrade             = 51;
				IsInstantiatedOnEachOptimizationIteration = true;

				// 01 Zeiten — standardmaessig AUS (ganztaegig)
				UseTimeWindow        = false;
				StartHour            = 15;
				StartMinute          = 30;
				EndHour              = 22;
				EndMinute            = 0;
				CloseAtWindowEnd     = false;
				// 02 Wochentage
				TradeMonday          = true;
				TradeTuesday         = true;
				TradeWednesday       = true;
				TradeThursday        = true;
				TradeFriday          = true;
				TradeSaturday        = true;  // fuer Krypto (24/7 bzw. CME-Sonntagssession);
				TradeSunday          = true;  // bei Index-Futures gibt es dort ohnehin keine Bars
				// 03 Einstieg
				BandEmaPeriod        = 50;
				BandStdDevPeriod     = 50;
				BandStdDevMultiple   = 2.0;
				EntryRequiresCloseInside = false; // Standard: Beruehrung (Docht) genuegt
				UseColorFilter       = false;     // gruene Signalkerze nur Long, rote nur Short
				UseOpenInsideBand    = false;     // Open der Signalkerze muss IN der Flaeche liegen
				AllowLong            = true;
				AllowShort           = true;
				RequirePdRange       = false;     // Long nur > PDH, Short nur < PDL — AUS
				// 04 Risiko
				AtrPeriod            = 14;
				InitialTargetR       = 2.0;
				UseTrailing          = true;      // Kern der Strategie
				UseFixedRisk         = true;
				RiskAmount           = 100;
				MaxContracts         = 50;
				Contracts            = 1;
				MinStopTicks         = 1;         // Waechter praktisch AUS
				MaxStopTicks         = 10000;     // Waechter praktisch AUS
				// 05 Verhalten
				MaxTradesPerDay      = 0;         // 0 = unbegrenzt (AUS)
				// 06 Diagnose
				EnableDebugLog       = false;
			}
			else if (State == State.Configure)
			{
				if (RequirePdRange)
					AddDataSeries(BarsPeriodType.Day, 1);   // nur fuer den PDH/PDL-Filter

				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade,
					Math.Max(BandEmaPeriod, Math.Max(BandStdDevPeriod, AtrPeriod)) + 1);
			}
			else if (State == State.DataLoaded)
			{
				bandEma = EMA(Close, BandEmaPeriod);
				bandSd  = StdDev(Close, BandStdDevPeriod);
				atr     = ATR(AtrPeriod);

				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
					Log(Name + ": Ausgelegt auf 1-Minuten-Kerzen (aktuell: " + BarsPeriod + ").", LogLevel.Warning);

				if (UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Groesse aus Geldrisiko nicht berechenbar, Rueckfall auf "
						+ Contracts + " Kontrakt(e).", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — Flaeche EMA" + BandEmaPeriod + " +/- " + BandStdDevMultiple
						+ " x SD" + BandStdDevPeriod + " | 1R = ATR" + AtrPeriod
						+ " | Ziel " + InitialTargetR + "R | Trailing: " + UseTrailing);
			}
		}

		// Einstiegs-Filter (alle optional, Standard AUS). Hier kommen nach und nach
		// weitere Indikatoren dazu — jeweils mit eigenem Schalter.
		private bool EntryFiltersOk(bool isLong, double upper, double lower)
		{
			// Kerzenfarbe: Die Signalkerze muss in Handelsrichtung schliessen —
			// gruen fuer Long, rot fuer Short (Rejection der Flaeche statt freier Fall hinein).
			if (UseColorFilter)
			{
				bool colorOk = isLong ? Close[0] > Open[0] : Close[0] < Open[0];
				if (!colorOk)
					return false;
			}

			// Open in der Flaeche: Die Signalkerze muss bereits IN der Flaeche EROEFFNET
			// haben — filtert Kerzen weg, die erst mit einem grossen Impuls hineinstuerzen.
			if (UseOpenInsideBand)
			{
				if (Open[0] > upper || Open[0] < lower)
					return false;
			}

			return true;
		}

		private int CalcQuantity(double stopDistance)
		{
			if (!UseFixedRisk)
				return Contracts;

			double pv = Instrument.MasterInstrument.PointValue;
			if (pv <= 0 || stopDistance <= 0)
				return Contracts;

			int qty = (int)Math.Floor(RiskAmount / (stopDistance * pv));
			return Math.Min(qty, MaxContracts);
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
				case DayOfWeek.Saturday:  return TradeSaturday;
				case DayOfWeek.Sunday:    return TradeSunday;
				default:                  return false;
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;
			if (CurrentBars[0] < BarsRequiredToTrade)
				return;
			if (RequirePdRange && (CurrentBars.Length < 2 || CurrentBars[1] < 1))
				return;                                    // Tagesserie noch nicht bereit

			if (Time[0].Date != currentDay)
			{
				currentDay  = Time[0].Date;
				tradesToday = 0;
			}

			double upper = bandEma[0] + BandStdDevMultiple * bandSd[0];
			double lower = bandEma[0] - BandStdDevMultiple * bandSd[0];

			// ---------- R-Leiter fuer die laufende Position ----------
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				if (UseTrailing)
					ManageTrail();
				// Scharfschaltung laeuft auch waehrend der Position weiter (unten),
				// eingestiegen wird aber erst wieder flat.
			}

			// ---------- Zeitfenster ----------
			TimeSpan barClose = Time[0].TimeOfDay;
			bool inWindow = true;
			if (UseTimeWindow)
			{
				TimeSpan ws = new TimeSpan(StartHour, StartMinute, 0);
				TimeSpan we = new TimeSpan(EndHour,   EndMinute,   0);
				inWindow = barClose > ws && barClose < we;

				if (CloseAtWindowEnd && barClose >= we && Position.MarketPosition != MarketPosition.Flat)
				{
					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("TimeExit", SignalLong);
					else
						ExitShort("TimeExit", SignalShort);
				}
			}

			bool dayOk = IsTradingDay(Time[0].DayOfWeek)
				&& (MaxTradesPerDay <= 0 || tradesToday < MaxTradesPerDay)
				&& Position.MarketPosition == MarketPosition.Flat
				&& inWindow;

			// ---------- Vortagesrange-Filter (optional) ----------
			bool pdLongOk = true, pdShortOk = true;
			if (RequirePdRange)
			{
				double pdh = Highs[1][0];
				double pdl = Lows[1][0];
				pdLongOk  = pdh > 0 && Close[0] > pdh;
				pdShortOk = pdl > 0 && Close[0] < pdl;
			}

			// ---------- Einstiege ----------
			if (dayOk)
			{
				// Long: Seite war scharf (Schluss ueber der Flaeche) und die Kerze kommt
				// von oben in die Flaeche — ohne komplett darunter zu schliessen.
				if (AllowLong && longArmed && pdLongOk && EntryFiltersOk(true, upper, lower))
				{
					bool touched = EntryRequiresCloseInside
						? (Close[0] <= upper && Close[0] >= lower)
						: (Low[0]  <= upper && Close[0] >= lower);
					if (touched && TryEnter(true))
					{
						longArmed = false;
						UpdateArming(upper, lower);
						return;
					}
				}

				// Short: spiegelbildlich von unten in die Flaeche.
				if (AllowShort && shortArmed && pdShortOk && EntryFiltersOk(false, upper, lower))
				{
					bool touched = EntryRequiresCloseInside
						? (Close[0] >= lower && Close[0] <= upper)
						: (High[0] >= lower && Close[0] <= upper);
					if (touched && TryEnter(false))
					{
						shortArmed = false;
						UpdateArming(upper, lower);
						return;
					}
				}
			}

			UpdateArming(upper, lower);
		}

		// Scharfschaltung NACH der Einstiegspruefung fortschreiben: Die Signalkerze
		// kann sich nicht selbst scharfschalten, es zaehlt der Zustand der Vorkerzen.
		private void UpdateArming(double upper, double lower)
		{
			if (Close[0] > upper) longArmed  = true;
			if (Close[0] < lower) shortArmed = true;
		}

		// Gibt true zurueck, wenn eine Order abgesetzt wurde.
		private bool TryEnter(bool isLong)
		{
			double r = atr[0];
			if (r <= 0)
				return false;

			if (r < MinStopTicks * TickSize || r > MaxStopTicks * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " EBR: verworfen — ATR "
						+ Math.Round(r / TickSize, 0) + " Ticks ausserhalb ["
						+ MinStopTicks + ".." + MaxStopTicks + "]");
				return false;
			}

			int qty = CalcQuantity(r);
			if (qty < 1)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " EBR: verworfen — Kontraktzahl 0 (ATR "
						+ Math.Round(r / TickSize, 0) + " Ticks)");
				return false;
			}

			// Vorlaeufige Level ab dem Signal-Close; nach dem Fill rechnet
			// OnExecutionUpdate alles auf den echten Einstiegskurs um.
			pendingR = r;
			double stop   = Instrument.MasterInstrument.RoundToTickSize(isLong ? Close[0] - r : Close[0] + r);
			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? Close[0] + InitialTargetR * r : Close[0] - InitialTargetR * r);

			if (isLong)
			{
				SetStopLoss(SignalLong, CalculationMode.Price, stop, false);
				SetProfitTarget(SignalLong, CalculationMode.Price, target);
				EnterLong(qty, SignalLong);
			}
			else
			{
				SetStopLoss(SignalShort, CalculationMode.Price, stop, false);
				SetProfitTarget(SignalShort, CalculationMode.Price, target);
				EnterShort(qty, SignalShort);
			}
			tradesToday++;

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " EBR: " + (isLong ? "LONG " : "SHORT ") + qty
					+ " @ " + Close[0] + " | 1R = " + Math.Round(r, 2)
					+ " | Stop " + stop + " | Ziel " + target + " | Trade " + tradesToday + " heute");
			return true;
		}

		// R-Leiter: Schliesst die Kerze jenseits der naechsten 1R-Stufe (ab Einstieg
		// gerechnet), wandern Stop und Ziel je 1R mit. Eine grosse Kerze kann mehrere
		// Stufen auf einmal schalten. Stufe k: Stop = Einstieg + (k-1)R,
		// Ziel = Einstieg + (k + InitialTargetR)R.
		private void ManageTrail()
		{
			if (activeR <= 0)
				return;

			bool isLong = Position.MarketPosition == MarketPosition.Long;
			int  steps  = 0;

			if (isLong)
			{
				while (Close[0] >= activeEntryPrice + (trailStep + 1) * activeR)
				{
					trailStep++;
					steps++;
					if (steps > 100) break;                 // Sicherheitsnetz
				}
				if (steps > 0)
				{
					double stop   = Instrument.MasterInstrument.RoundToTickSize(
						activeEntryPrice + (trailStep - 1) * activeR);
					double target = Instrument.MasterInstrument.RoundToTickSize(
						activeEntryPrice + (trailStep + InitialTargetR) * activeR);
					stop = Math.Min(stop, Close[0] - TickSize);   // Stop bleibt unter dem Markt
					SetStopLoss(SignalLong, CalculationMode.Price, stop, false);
					SetProfitTarget(SignalLong, CalculationMode.Price, target);
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " EBR: Stufe " + trailStep
							+ " — Stop " + stop + " | Ziel " + target);
				}
			}
			else
			{
				while (Close[0] <= activeEntryPrice - (trailStep + 1) * activeR)
				{
					trailStep++;
					steps++;
					if (steps > 100) break;
				}
				if (steps > 0)
				{
					double stop   = Instrument.MasterInstrument.RoundToTickSize(
						activeEntryPrice - (trailStep - 1) * activeR);
					double target = Instrument.MasterInstrument.RoundToTickSize(
						activeEntryPrice - (trailStep + InitialTargetR) * activeR);
					stop = Math.Max(stop, Close[0] + TickSize);
					SetStopLoss(SignalShort, CalculationMode.Price, stop, false);
					SetProfitTarget(SignalShort, CalculationMode.Price, target);
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " EBR: Stufe " + trailStep
							+ " — Stop " + stop + " | Ziel " + target);
				}
			}
		}

		// Nach dem echten Fill: 1R ab Einstiegskurs neu verankern — Stop = Fill - 1R,
		// Ziel = Fill + InitialTargetR x R. Damit stimmt die R-Leiter exakt zum Fill.
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null)
				return;
			if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
				return;

			bool isLong = execution.Order.Name == SignalLong;
			if (!isLong && execution.Order.Name != SignalShort)
				return;
			if (pendingR <= 0)
				return;

			activeEntryPrice = price;
			activeR          = pendingR;
			trailStep        = 0;

			double stop   = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price - activeR : price + activeR);
			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price + InitialTargetR * activeR : price - InitialTargetR * activeR);

			if (isLong)
			{
				SetStopLoss(SignalLong, CalculationMode.Price, stop, false);
				SetProfitTarget(SignalLong, CalculationMode.Price, target);
			}
			else
			{
				SetStopLoss(SignalShort, CalculationMode.Price, stop, false);
				SetProfitTarget(SignalShort, CalculationMode.Price, target);
			}

			if (EnableDebugLog)
				Print(time.ToString("yyyy-MM-dd HH:mm") + " EBR: Fill @ " + price + " x" + quantity
					+ " | 1R = " + Math.Round(activeR, 2) + " | Stop " + stop + " | Ziel " + target);
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Zeitfenster aktiv", Description = "AUS (Standard): ganztaegig handeln. AN: Einstiege nur zwischen Start und Ende.", Order = 1, GroupName = "01 Zeiten")]
		public bool UseTimeWindow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Start – Stunde", Order = 2, GroupName = "01 Zeiten")]
		public int StartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Start – Minute", Order = 3, GroupName = "01 Zeiten")]
		public int StartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Ende – Stunde", Order = 4, GroupName = "01 Zeiten")]
		public int EndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Ende – Minute", Order = 5, GroupName = "01 Zeiten")]
		public int EndMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Zum Fensterende glattstellen", Description = "Nur wenn Zeitfenster aktiv.", Order = 6, GroupName = "01 Zeiten")]
		public bool CloseAtWindowEnd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Montag handeln",    Order = 10, GroupName = "02 Wochentage")]
		public bool TradeMonday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dienstag handeln",  Order = 11, GroupName = "02 Wochentage")]
		public bool TradeTuesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mittwoch handeln",  Order = 12, GroupName = "02 Wochentage")]
		public bool TradeWednesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Donnerstag handeln", Order = 13, GroupName = "02 Wochentage")]
		public bool TradeThursday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Freitag handeln",   Order = 14, GroupName = "02 Wochentage")]
		public bool TradeFriday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Samstag handeln", Description = "Nur fuer 24/7-Maerkte (Krypto-Spot/Perp) relevant.", Order = 15, GroupName = "02 Wochentage")]
		public bool TradeSaturday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sonntag handeln", Description = "Krypto und CME-Sonntagssession. Bei Instrumenten ohne Sonntags-Bars wirkungslos.", Order = 16, GroupName = "02 Wochentage")]
		public bool TradeSunday { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Flaeche: EMA-Periode", Description = "Standard 50.", Order = 20, GroupName = "03 Einstieg")]
		public int BandEmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Flaeche: StdAbw-Periode", Description = "Periode der Standardabweichung. Standard 50 (gleich der EMA).", Order = 21, GroupName = "03 Einstieg")]
		public int BandStdDevPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 10)]
		[Display(Name = "Flaeche: StdAbw-Faktor", Description = "Breite der Flaeche: EMA +/- Faktor x Standardabweichung. Standard 2.", Order = 22, GroupName = "03 Einstieg")]
		public double BandStdDevMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Schluss in der Flaeche noetig", Description = "AUS (Standard): Beruehrung der Flaeche mit dem Docht genuegt als Signal. AN: die Kerze muss IN der Flaeche schliessen.", Order = 23, GroupName = "03 Einstieg")]
		public bool EntryRequiresCloseInside { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Kerzenfarben-Filter", Description = "AN: Nur Longs, wenn die Signalkerze gruen schliesst; nur Shorts bei roter Signalkerze. Standard AUS.", Order = 27, GroupName = "03 Einstieg")]
		public bool UseColorFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Open in der Flaeche noetig", Description = "AN: Die Signalkerze muss bereits INNERHALB der Flaeche eroeffnet haben. Standard AUS.", Order = 28, GroupName = "03 Einstieg")]
		public bool UseOpenInsideBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben",  Order = 24, GroupName = "03 Einstieg")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Order = 25, GroupName = "03 Einstieg")]
		public bool AllowShort { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vortagesrange-Filter", Description = "AN: Long nur, wenn der Kurs ueber dem Vortageshoch steht; Short nur unter dem Vortagestief. Standard AUS.", Order = 26, GroupName = "03 Einstieg")]
		public bool RequirePdRange { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "ATR-Periode (1R)", Description = "1R = ATR-Wert bei Signal. Standard 14.", Order = 30, GroupName = "04 Risiko")]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 20)]
		[Display(Name = "Start-Ziel (R)", Description = "Anfangsziel in R. Standard 2 — mit Trailing wandert es je Stufe 1R weiter.", Order = 31, GroupName = "04 Risiko")]
		public double InitialTargetR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "R-Leiter-Trailing aktiv", Description = "AN (Standard): Ab +1R (Schlusskurs) wandern Stop und Ziel je 1R mit. AUS: festes Bracket Stop 1R / Ziel wie eingestellt.", Order = 32, GroupName = "04 Risiko")]
		public bool UseTrailing { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein 1R-Stopout etwa dem Betrag unten entspricht.", Order = 33, GroupName = "04 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Order = 34, GroupName = "04 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Order = 35, GroupName = "04 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Kontrakte (fest)", Description = "Nur wenn 'Groesse aus Geldrisiko' aus ist.", Order = 36, GroupName = "04 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Mindest-Stopdistanz (Ticks)", Description = "Signale mit engerem ATR verwerfen. Standard 1 = praktisch aus.", Order = 37, GroupName = "04 Risiko")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100000)]
		[Display(Name = "Maximale Stopdistanz (Ticks)", Description = "Signale mit weiterem ATR verwerfen. Standard 10000 = praktisch aus.", Order = 38, GroupName = "04 Risiko")]
		public int MaxStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt (Standard).", Order = 40, GroupName = "05 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt Einstiege, verworfene Signale und jede Trailing-Stufe ins Output-Fenster.", Order = 50, GroupName = "06 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
