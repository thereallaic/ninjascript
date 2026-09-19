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
// PrevDayRangeBreakout  —  Ausbruch ueber das Vortageshoch / unter das Vortagestief
//                          NinjaTrader 8.1, ausgelegt auf 1-Minuten-Kerzen
//
// Grundgedanke: Vortageshoch (PDH) und Vortagestief (PDL) sind Referenzlevel, die viele
// Marktteilnehmer beobachten. Long wird nur OBERHALB des PDH erlaubt, Short nur
// UNTERHALB des PDL. Das Level ist damit eine ERLAUBNIS — der eigentliche Ausloeser
// steckt in EntryMode und ist bewusst umschaltbar.
//
// Regelwerk:
//  1. Handelsfenster 15:30–17:00 (US-Kassaeroeffnung, lokale NT-Zeitzone).
//  2. Referenzlevel aus der letzten ABGESCHLOSSENEN Tageskerze: PDH = Highs[1][0],
//     PDL = Lows[1][0]. NinjaTrader liefert von Zusatzserien nur fertige Bars, der
//     heutige Tag fliesst also nicht ein — kein Blick in die Zukunft.
//  3. EINSTIEGSMODUS (EntryMode):
//       1 = AUSBRUCH: erste Kerze im Fenster, die jenseits des Levels SCHLIESST.
//       2 = RETEST:   nach einem solchen Ausbruch muss der Kurs das Level noch einmal
//                     beruehren (Low <= PDH + Toleranz) und darueber schliessen.
//  3b. VECTOR CANDLE (RequireVectorCandle, Standard an): Die Signalkerze muss
//     mindestens VectorVolumeMultiple (Standard 2,0 = 200 %) des Durchschnittsvolumens
//     der VectorVolumeLookback VORHERGEHENDEN Kerzen haben UND in Handelsrichtung
//     schliessen — bei Long gruen, bei Short rot. Der Durchschnitt wird ueber
//     volAvg[1] gelesen, die Signalkerze zaehlt also NICHT in ihre eigene Messlatte.
//  4. FRISCHER AUSBRUCH (RequireFreshBreak, Standard an): Der Kurs muss im Fenster
//     mindestens einmal DIESSEITS des Levels geschlossen haben, bevor der Ausbruch
//     zaehlt. Ohne diese Bedingung wuerde die Strategie an Tagen, an denen der Kurs
//     schon um 15:30 oberhalb steht, sofort um 15:31 einsteigen — das waere ein
//     Zeit-Einstieg mit Level-Filter, nicht mehr ein Ausbruch.
//  5. Stop (StopAtLevel, Standard an): auf dem gebrochenen Level +/- Offset. Damit ist
//     der Stop strukturell begruendet und passt sich der Lage an. Alternativ feste
//     Distanz aus StopTicks. MinStopTicks / MaxStopTicks verwerfen Signale, deren
//     Abstand zu eng (Rauschen) oder zu weit (schlechtes R) ist.
//  6. Ziel = RewardMultiple x Stopdistanz. Zum Durchtesten von 1R bis 3R gedacht.
//  7. Positionsgroesse aus festem Geldrisiko. Max. MaxTradesPerDay Trades pro Tag.
//  8. Offene Position wird um 17:00 glattgestellt (CloseAtWindowEnd).
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// Hinweis: PDH/PDL stammen aus der Tageskerze gemaess dem eingestellten Trading-Hours-
// Template. Bei Futures umfasst die Tageskerze die volle Session, nicht nur die Kassazeit.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class PrevDayRangeBreakout : Strategy
	{
		private const string SignalLong  = "PDR_Long";
		private const string SignalShort = "PDR_Short";

		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;

		// Tageszustand relativ zu den Leveln
		private bool   longArmed;    // Kurs schloss im Fenster schon mal AUF/UNTER PDH
		private bool   shortArmed;   // Kurs schloss im Fenster schon mal AUF/UEBER PDL
		private bool   longBroke;    // eine Kerze schloss ueber PDH
		private bool   shortBroke;   // eine Kerze schloss unter PDL

		private SMA    volAvg;       // Durchschnittsvolumen der vorhergehenden Kerzen
		private double activeStopLevel;
		private double activeStopDistance;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Ausbruch ueber das Vortageshoch bzw. unter das Vortagestief im US-Fenster 15:30-17:00. Das Level ist die Erlaubnis, der Ausloeser ist ueber EntryMode umschaltbar (Ausbruch oder Retest). Stop strukturell am Level, Ziel als frei einstellbares R.";
				Name                            = "PrevDayRangeBreakout";
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
				BarsRequiredToTrade             = 1;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Zeiten
				StartHour            = 15;
				StartMinute          = 30;
				EndHour              = 17;
				EndMinute            = 0;
				CloseAtWindowEnd     = true;
				// Wochentage
				TradeMonday          = true;
				TradeTuesday         = true;
				TradeWednesday       = true;
				TradeThursday        = true;
				TradeFriday          = true;
				// Einstieg
				EntryMode            = 1;     // 1 = Ausbruch, 2 = Retest
				RequireVectorCandle  = true;  // Ausloeser: Vector Candle
				VectorVolumeMultiple = 2.0;   // 200 % des Durchschnitts
				VectorVolumeLookback = 20;    // ... der 20 VORHERGEHENDEN Kerzen
				RequireFreshBreak    = true;
				RetestToleranceTicks = 4;
				AllowLong            = true;
				AllowShort           = true;
				// Risiko
				StopAtLevel          = true;
				StopOffsetTicks      = 4;
				StopTicks            = 40;    // nur bei StopAtLevel = false
				MinStopTicks         = 8;
				MaxStopTicks         = 120;
				RewardMultiple       = 2;     // Bereich zum Durchtesten: 1 bis 3
				UseFixedRisk         = true;
				RiskAmount           = 100;
				MaxContracts         = 50;
				Contracts            = 1;
				MaxTradesPerDay      = 1;
				EnableDebugLog       = false;
			}
			else if (State == State.Configure)
			{
				// Tagesserie fuer PDH/PDL — Pflicht, nicht optional
				AddDataSeries(BarsPeriodType.Day, 1);

				if (RequireVectorCandle)
					BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, VectorVolumeLookback + 1);
			}
			else if (State == State.DataLoaded)
			{
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
					Log(Name + ": Bitte eine 1-Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + ").", LogLevel.Warning);

				if (RequireVectorCandle)
					volAvg = SMA(Volume, VectorVolumeLookback);

				if (UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Positionsgroesse aus Geldrisiko nicht berechenbar, es wird auf "
						+ Contracts + " Kontrakt(e) zurueckgefallen.", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — " + Instrument.FullName
						+ " | Fenster " + StartHour.ToString("00") + ":" + StartMinute.ToString("00")
						+ "-" + EndHour.ToString("00") + ":" + EndMinute.ToString("00")
						+ " | Modus " + (EntryMode == 2 ? "RETEST" : "AUSBRUCH")
						+ " | frischer Ausbruch: " + RequireFreshBreak
						+ " | Ziel " + RewardMultiple + "R");
			}
		}

		// Vector Candle: hohes Volumen UND Koerper in Handelsrichtung.
		//
		// Der Durchschnitt wird ueber volAvg[1] gelesen, also ueber die VORHERGEHENDEN
		// Kerzen ohne die aktuelle. Sonst zoege eine Volumenspitze ihre eigene Messlatte
		// nach oben und das Signal wuerde umso schwaecher, je staerker der Ausbruch ist.
		private bool IsVectorCandle(bool isLong)
		{
			if (!RequireVectorCandle)
				return true;
			if (volAvg == null || CurrentBars[0] < VectorVolumeLookback + 1)
				return false;

			double avg = volAvg[1];
			if (avg <= 0)
				return false;

			bool volumeOk = Volume[0] >= VectorVolumeMultiple * avg;
			bool colorOk  = isLong ? Close[0] > Open[0] : Close[0] < Open[0];
			return volumeOk && colorOk;
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
				default:                  return false;
			}
		}

		private void ResetDay(DateTime day)
		{
			currentDay  = day;
			tradesToday = 0;
			longArmed   = false;
			shortArmed  = false;
			longBroke   = false;
			shortBroke  = false;
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;
			if (CurrentBars[0] < 1 || CurrentBars.Length < 2 || CurrentBars[1] < 1)
				return;                                    // noch keine abgeschlossene Tageskerze

			if (Time[0].Date != currentDay)
				ResetDay(Time[0].Date);

			TimeSpan barClose    = Time[0].TimeOfDay;
			TimeSpan windowStart = new TimeSpan(StartHour, StartMinute, 0);
			TimeSpan windowEnd   = new TimeSpan(EndHour,   EndMinute,   0);

			// ---------- Fensterende: glattstellen ----------
			if (barClose >= windowEnd)
			{
				if (CloseAtWindowEnd && Position.MarketPosition != MarketPosition.Flat)
				{
					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("TimeExit", SignalLong);
					else
						ExitShort("TimeExit", SignalShort);

					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: Zeit-Ausstieg @ " + Close[0]);
				}
				return;
			}

			if (barClose <= windowStart)
				return;                                    // vor dem Fenster passiert nichts

			// Vortageshoch / -tief aus der letzten ABGESCHLOSSENEN Tageskerze
			double pdh = Highs[1][0];
			double pdl = Lows[1][0];
			if (pdh <= 0 || pdl <= 0 || pdh <= pdl)
				return;

			bool dayOk = IsTradingDay(Time[0].DayOfWeek)
			             && (MaxTradesPerDay <= 0 || tradesToday < MaxTradesPerDay)
			             && Position.MarketPosition == MarketPosition.Flat;

			if (dayOk)
			{
				// ---------- Long: oberhalb des Vortageshochs ----------
				if (AllowLong && (longArmed || !RequireFreshBreak))
				{
					bool levelOk = EntryMode == 2
						? (longBroke && Low[0] <= pdh + RetestToleranceTicks * TickSize && Close[0] > pdh)
						: (Close[0] > pdh);

					if (levelOk && !IsVectorCandle(true))
						LogVectorMiss(true);
					else if (levelOk && TryEnter(true, pdh))
						return;
				}

				// ---------- Short: unterhalb des Vortagestiefs ----------
				if (AllowShort && (shortArmed || !RequireFreshBreak))
				{
					bool levelOk = EntryMode == 2
						? (shortBroke && High[0] >= pdl - RetestToleranceTicks * TickSize && Close[0] < pdl)
						: (Close[0] < pdl);

					if (levelOk && !IsVectorCandle(false))
						LogVectorMiss(false);
					else if (levelOk && TryEnter(false, pdl))
						return;
				}
			}

			// ---------- Zustand fuer die NAECHSTE Kerze fortschreiben ----------
			// Bewusst am Ende: Der "frische Ausbruch" muss sich auf eine FRUEHERE Kerze
			// stuetzen, sonst wuerde dieselbe Kerze sich selbst scharfschalten.
			if (Close[0] <= pdh) longArmed  = true;
			if (Close[0] >  pdh) longBroke  = true;
			if (Close[0] >= pdl) shortArmed = true;
			if (Close[0] <  pdl) shortBroke = true;
		}

		// Nur protokollieren, wenn das Level bereits passte — sonst waere der Log voll
		// mit Kerzen, die ohnehin nie in Frage kamen.
		private void LogVectorMiss(bool isLong)
		{
			if (!EnableDebugLog)
				return;
			double avg = volAvg != null && CurrentBars[0] >= VectorVolumeLookback + 1 ? volAvg[1] : 0;
			bool colorOk = isLong ? Close[0] > Open[0] : Close[0] < Open[0];
			Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: Level ok, aber keine Vector Candle ("
				+ (isLong ? "Long" : "Short") + ") — Vol " + Volume[0]
				+ " vs. " + Math.Round(VectorVolumeMultiple * avg, 0) + " noetig"
				+ (colorOk ? "" : ", Farbe passt nicht"));
		}

		// Gibt true zurueck, wenn tatsaechlich eine Order abgesetzt wurde.
		private bool TryEnter(bool isLong, double level)
		{
			double stopLevel = StopAtLevel
				? (isLong ? level - StopOffsetTicks * TickSize : level + StopOffsetTicks * TickSize)
				: (isLong ? Close[0] - StopTicks * TickSize   : Close[0] + StopTicks * TickSize);
			stopLevel = Instrument.MasterInstrument.RoundToTickSize(stopLevel);

			double dist = isLong ? Close[0] - stopLevel : stopLevel - Close[0];

			if (dist < MinStopTicks * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Stop zu eng ("
						+ Math.Round(dist / TickSize, 0) + " Ticks < " + MinStopTicks + ")");
				return false;
			}
			if (dist > MaxStopTicks * TickSize)
			{
				// Tritt auf, wenn der Kurs beim Ausloeser schon weit vom Level weg ist —
				// dann waere das R-Ziel unrealistisch weit entfernt.
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Stop zu weit ("
						+ Math.Round(dist / TickSize, 0) + " Ticks > " + MaxStopTicks + ")");
				return false;
			}

			int qty = CalcQuantity(dist);
			if (qty < 1)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Kontraktzahl 0 (Stop "
						+ Math.Round(dist / TickSize, 0) + " Ticks)");
				return false;
			}

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? Close[0] + RewardMultiple * dist : Close[0] - RewardMultiple * dist);

			activeStopLevel    = stopLevel;
			activeStopDistance = dist;

			if (isLong)
			{
				SetStopLoss(SignalLong, CalculationMode.Price, stopLevel, false);
				SetProfitTarget(SignalLong, CalculationMode.Price, target);
				EnterLong(qty, SignalLong);
			}
			else
			{
				SetStopLoss(SignalShort, CalculationMode.Price, stopLevel, false);
				SetProfitTarget(SignalShort, CalculationMode.Price, target);
				EnterShort(qty, SignalShort);
			}
			tradesToday++;

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: " + (isLong ? "LONG " : "SHORT ") + qty
					+ " @ " + Close[0] + " | Level " + Math.Round(level, 2)
					+ " | Stop " + stopLevel + " (" + Math.Round(dist / TickSize, 0) + " Ticks)"
					+ " | Ziel " + target + " | Trade " + tradesToday + " heute");
			return true;
		}

		// Stop bleibt auf dem strukturellen Level; das Ziel wird auf den echten Fill
		// nachgerechnet, damit das R-Verhaeltnis auch nach Slippage stimmt.
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

			double risk = isLong ? price - activeStopLevel : activeStopLevel - price;
			if (risk < TickSize)
				return;

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price + RewardMultiple * risk : price - RewardMultiple * risk);

			if (isLong)
			{
				SetStopLoss(SignalLong, CalculationMode.Price, activeStopLevel, false);
				SetProfitTarget(SignalLong, CalculationMode.Price, target);
			}
			else
			{
				SetStopLoss(SignalShort, CalculationMode.Price, activeStopLevel, false);
				SetProfitTarget(SignalShort, CalculationMode.Price, target);
			}

			if (EnableDebugLog)
				Print(time.ToString("yyyy-MM-dd HH:mm") + " PDR: Fill @ " + price + " x" + quantity
					+ " | Stop " + activeStopLevel + " | Ziel " + target
					+ " | Risiko " + Math.Round(risk * Instrument.MasterInstrument.PointValue * quantity, 2));
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Start – Stunde", Description = "Beginn des Handelsfensters (lokale NT-Zeitzone)", Order = 1, GroupName = "01 Zeiten")]
		public int StartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Start – Minute", Order = 2, GroupName = "01 Zeiten")]
		public int StartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Ende – Stunde", Description = "Danach keine neuen Einstiege.", Order = 3, GroupName = "01 Zeiten")]
		public int EndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Ende – Minute", Order = 4, GroupName = "01 Zeiten")]
		public int EndMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Zum Fensterende glattstellen", Description = "True (Standard): offene Position wird am Fensterende geschlossen. False: laeuft bis Stop oder Ziel.", Order = 5, GroupName = "01 Zeiten")]
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
		[Range(1, 2)]
		[Display(Name = "Einstiegsmodus (1=Ausbruch, 2=Retest)", Description = "1: erste Kerze, die jenseits des Levels schliesst. 2: nach dem Ausbruch muss der Kurs das Level noch einmal beruehren und darueber/darunter schliessen.", Order = 20, GroupName = "03 Einstieg")]
		public int EntryMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vector Candle noetig", Description = "True (Standard): Die Signalkerze muss mindestens das eingestellte Vielfache des Durchschnittsvolumens haben UND in Handelsrichtung schliessen (Long gruen, Short rot). False: reiner Level-Ausbruch ohne Volumenbedingung — der Vergleichslauf.", Order = 25, GroupName = "03 Einstieg")]
		public bool RequireVectorCandle { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 20)]
		[Display(Name = "Vector: Volumen-Faktor", Description = "Vielfaches des Durchschnittsvolumens. 2,0 = 200 Prozent (Standard).", Order = 26, GroupName = "03 Einstieg")]
		public double VectorVolumeMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Vector: Durchschnitt ueber", Description = "Ueber wie viele VORHERGEHENDE Kerzen der Volumendurchschnitt gebildet wird. Die Signalkerze selbst zaehlt nicht mit. Standard 20.", Order = 27, GroupName = "03 Einstieg")]
		public int VectorVolumeLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Frischer Ausbruch noetig", Description = "True (Standard): Der Kurs muss im Fenster erst diesseits des Levels geschlossen haben. False: Es genuegt, jenseits zu stehen — dann wird an Tagen mit Kurs bereits ueber PDH sofort um 15:31 eingestiegen.", Order = 21, GroupName = "03 Einstieg")]
		public bool RequireFreshBreak { get; set; }

		[NinjaScriptProperty]
		[Range(0, 200)]
		[Display(Name = "Retest-Toleranz (Ticks)", Description = "Wie nah der Kurs im Retest-Modus ans Level zurueck muss. Nur bei Modus 2.", Order = 22, GroupName = "03 Einstieg")]
		public int RetestToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben",  Description = "Einstiege oberhalb des Vortageshochs zulassen.", Order = 23, GroupName = "03 Einstieg")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Description = "Einstiege unterhalb des Vortagestiefs zulassen.", Order = 24, GroupName = "03 Einstieg")]
		public bool AllowShort { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop am Level", Description = "True (Standard): Stop auf dem gebrochenen Vortageslevel +/- Offset — strukturell begruendet. False: feste Distanz aus 'Stopdistanz (Ticks)'.", Order = 30, GroupName = "04 Risiko")]
		public bool StopAtLevel { get; set; }

		[NinjaScriptProperty]
		[Range(0, 200)]
		[Display(Name = "Stop-Offset (Ticks)", Description = "Puffer hinter dem Level. Nur bei 'Stop am Level'.", Order = 31, GroupName = "04 Risiko")]
		public int StopOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2000)]
		[Display(Name = "Stopdistanz (Ticks)", Description = "Feste Stopdistanz. Nur wenn 'Stop am Level' aus ist.", Order = 32, GroupName = "04 Risiko")]
		public int StopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Stopdistanz (Ticks)", Description = "Signale mit engerem Stop verwerfen — schuetzt vor Mini-Stops im Rauschen und vor absurden Positionsgroessen.", Order = 33, GroupName = "04 Risiko")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5000)]
		[Display(Name = "Maximale Stopdistanz (Ticks)", Description = "Signale mit weiterem Stop verwerfen. Greift, wenn der Kurs beim Ausloeser schon weit vom Level entfernt ist — dann waere das R-Ziel unrealistisch weit weg.", Order = 34, GroupName = "04 Risiko")]
		public int MaxStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Ziel = Einstieg +/- Multiple x Stopdistanz. Zum Durchtesten von 1 bis 3 gedacht.", Order = 35, GroupName = "04 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein Stopout etwa dem Betrag unten entspricht.", Order = 36, GroupName = "04 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Description = "Geldbetrag in Instrumentenwaehrung, den ein Stopout kostet.", Order = 37, GroupName = "04 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Order = 38, GroupName = "04 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Kontrakte (fest)", Description = "Nur wenn 'Groesse aus Geldrisiko' aus ist.", Order = 39, GroupName = "04 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt. Standard 1.", Order = 40, GroupName = "05 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jeden Einstieg, jedes verworfene Signal mit Grund und jeden Zeit-Ausstieg ins NinjaScript Output-Fenster.", Order = 50, GroupName = "06 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
