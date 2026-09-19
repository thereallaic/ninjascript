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
// PrevDayRangeBreakout  —  Vector Candle jenseits des Vortageshochs / Vortagestiefs
//                          NinjaTrader 8.1, bewusst einfach gehalten
//
// Regelwerk (vollstaendig):
//  1. Handelsfenster: Einstiege nur zwischen 15:30 und 17:00 (lokale NT-Zeitzone).
//  2. Referenzlevel aus der letzten ABGESCHLOSSENEN Tageskerze: PDH = Highs[1][0],
//     PDL = Lows[1][0]. NinjaTrader liefert von Zusatzserien nur fertige Bars, der
//     heutige Tag fliesst also nicht ein — kein Blick in die Zukunft.
//  3. Long nur, wenn die Kerze OBERHALB des PDH schliesst; Short nur, wenn sie
//     UNTERHALB des PDL schliesst. Das Level ist die Erlaubnis.
//  4. Ausloeser ist eine VECTOR CANDLE: Volumen mindestens VectorVolumeMultiple
//     (Standard 2,0 = 200 %) des Durchschnittsvolumens der VectorVolumeLookback
//     VORHERGEHENDEN Kerzen (volAvg[1] — die Signalkerze zaehlt nicht in ihre eigene
//     Messlatte) UND Schluss in Handelsrichtung: Long gruen, Short rot.
//  5. Stop-Loss auf dem OPEN der Signalkerze. Bei einer gruenen Kerze liegt das Open
//     unter dem Close, bei einer roten darueber — der Stop sitzt also automatisch auf
//     der richtigen Seite. MinStopTicks / MaxStopTicks verwerfen Signale, deren
//     Abstand zu eng (Rauschen, absurde Positionsgroesse) oder zu weit ist.
//  6. Ziel = RewardMultiple x Stopdistanz (Standard 3R), nach dem Fill auf den echten
//     Einstiegskurs nachgerechnet.
//  7. Positionsgroesse aus festem Geldrisiko. Max. MaxTradesPerDay Trades pro Tag.
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

		private SMA    volAvg;       // Durchschnittsvolumen der vorhergehenden Kerzen
		private double activeStopLevel;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Vector Candle (200 % Volumen, Farbe in Handelsrichtung) oberhalb des Vortageshochs bzw. unterhalb des Vortagestiefs, 15:30-17:00. Stop auf dem Open der Signalkerze, Ziel 3R.";
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
				VectorVolumeMultiple = 2.0;   // 200 % des Durchschnitts
				VectorVolumeLookback = 20;    // ... der 20 VORHERGEHENDEN Kerzen
				AllowLong            = true;
				AllowShort           = true;
				// Risiko
				MinStopTicks         = 8;
				MaxStopTicks         = 120;
				RewardMultiple       = 3;
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

				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, VectorVolumeLookback + 1);
			}
			else if (State == State.DataLoaded)
			{
				volAvg = SMA(Volume, VectorVolumeLookback);

				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
					Log(Name + ": Bitte eine Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + ").", LogLevel.Warning);

				if (UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Positionsgroesse aus Geldrisiko nicht berechenbar, es wird auf "
						+ Contracts + " Kontrakt(e) zurueckgefallen.", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — " + Instrument.FullName
						+ " | Fenster " + StartHour.ToString("00") + ":" + StartMinute.ToString("00")
						+ "-" + EndHour.ToString("00") + ":" + EndMinute.ToString("00")
						+ " | Vector " + (VectorVolumeMultiple * 100) + " % ueber " + VectorVolumeLookback + " Kerzen"
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

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;
			if (CurrentBars[0] < 1 || CurrentBars.Length < 2 || CurrentBars[1] < 1)
				return;                                    // noch keine abgeschlossene Tageskerze

			if (Time[0].Date != currentDay)
			{
				currentDay  = Time[0].Date;
				tradesToday = 0;
			}

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

			if (!IsTradingDay(Time[0].DayOfWeek))
				return;
			if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
				return;
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// ---------- Long: Vector Candle schliesst oberhalb des Vortageshochs ----------
			if (AllowLong && Close[0] > pdh && IsVectorCandle(true))
			{
				TryEnter(true);
				return;
			}

			// ---------- Short: Vector Candle schliesst unterhalb des Vortagestiefs ----------
			if (AllowShort && Close[0] < pdl && IsVectorCandle(false))
				TryEnter(false);
		}

		// Stop = Open der Signalkerze. Distanz fuer Groesse und Ziel wird zunaechst ab
		// dem Signal-Close gerechnet; nach dem Fill rechnet OnExecutionUpdate das Ziel
		// auf den echten Einstiegskurs nach.
		private void TryEnter(bool isLong)
		{
			double stopLevel = Instrument.MasterInstrument.RoundToTickSize(Open[0]);
			double dist      = isLong ? Close[0] - stopLevel : stopLevel - Close[0];

			if (dist < MinStopTicks * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Stop zu eng ("
						+ Math.Round(dist / TickSize, 0) + " Ticks < " + MinStopTicks + ")");
				return;
			}
			if (dist > MaxStopTicks * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Stop zu weit ("
						+ Math.Round(dist / TickSize, 0) + " Ticks > " + MaxStopTicks + ")");
				return;
			}

			int qty = CalcQuantity(dist);
			if (qty < 1)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDR: verworfen — Kontraktzahl 0 (Stop "
						+ Math.Round(dist / TickSize, 0) + " Ticks)");
				return;
			}

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? Close[0] + RewardMultiple * dist : Close[0] - RewardMultiple * dist);

			activeStopLevel = stopLevel;

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
					+ " @ " + Close[0]
					+ " | Stop (Kerzen-Open) " + stopLevel + " (" + Math.Round(dist / TickSize, 0) + " Ticks)"
					+ " | Ziel " + target + " | Trade " + tradesToday + " heute");
		}

		// Stop bleibt auf dem Open der Signalkerze; das Ziel wird auf den echten Fill
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
		[Range(1.0, 20)]
		[Display(Name = "Vector: Volumen-Faktor", Description = "Vielfaches des Durchschnittsvolumens. 2,0 = 200 Prozent (Standard).", Order = 20, GroupName = "03 Einstieg")]
		public double VectorVolumeMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Vector: Durchschnitt ueber", Description = "Ueber wie viele VORHERGEHENDE Kerzen der Volumendurchschnitt gebildet wird. Die Signalkerze selbst zaehlt nicht mit. Standard 20.", Order = 21, GroupName = "03 Einstieg")]
		public int VectorVolumeLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben",  Description = "Einstiege oberhalb des Vortageshochs zulassen.", Order = 22, GroupName = "03 Einstieg")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Description = "Einstiege unterhalb des Vortagestiefs zulassen.", Order = 23, GroupName = "03 Einstieg")]
		public bool AllowShort { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Stopdistanz (Ticks)", Description = "Signale mit engerem Stop verwerfen — schuetzt vor Mini-Stops im Rauschen und vor absurden Positionsgroessen.", Order = 30, GroupName = "04 Risiko")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5000)]
		[Display(Name = "Maximale Stopdistanz (Ticks)", Description = "Signale mit weiterem Stop verwerfen — bei einer sehr grossen Signalkerze waere das R-Ziel unrealistisch weit weg.", Order = 31, GroupName = "04 Risiko")]
		public int MaxStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Ziel = Einstieg +/- Multiple x Stopdistanz. Standard 3.", Order = 32, GroupName = "04 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein Stopout etwa dem Betrag unten entspricht.", Order = 33, GroupName = "04 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Description = "Geldbetrag in Instrumentenwaehrung, den ein Stopout kostet.", Order = 34, GroupName = "04 Risiko")]
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
		[Range(0, 50)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt. Standard 1.", Order = 40, GroupName = "05 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jeden Einstieg, jedes verworfene Signal mit Grund und jeden Zeit-Ausstieg ins NinjaScript Output-Fenster.", Order = 50, GroupName = "06 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
