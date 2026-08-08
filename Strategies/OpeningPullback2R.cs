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
//  1. Referenzkurs = Open der 1-Minuten-Kerze um 09:00 (Xetra-Eroeffnung).
//  2. Die ersten 5 Kerzen (09:00–09:05) werden nur beobachtet.
//     Schlusskurs der 5. Kerze > Referenz  -> Long-Bias
//     Schlusskurs der 5. Kerze < Referenz  -> Short-Bias
//  3. Long:  Einstieg per Market beim Schluss der ERSTEN roten Kerze nach dem Fenster
//            (fruehester Einstieg somit 09:06:00).
//     Short: spiegelbildlich, erste gruene Kerze.
//  4. Stop (Long)  = tiefster Close einer ROTEN Kerze der 5 Anfangskerzen − Offset.
//     Stop (Short) = hoechster Close einer GRUENEN Kerze der 5 Anfangskerzen + Offset.
//     R = Distanz Einstieg->Stop.  Take-Profit = Einstieg +/- RewardMultiple * R.
//  5. Maximal 1 Trade pro Tag. Kein Einstieg mehr nach der Cutoff-Zeit.
//
// Wichtig: 1-Minuten-Datenserie verwenden. Zeitstempel = Schlusszeit der Kerze
// (NT8-Standard). Der PC / NinjaTrader muss auf Zeitzone Berlin stehen, damit
// "09:00" auch die Xetra-Eroeffnung trifft (Tools > Options > General > Time zone).
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
		private double   lowestRedClose;     // tiefster Close einer roten Kerze im Fenster
		private double   highestGreenClose;  // hoechster Close einer gruenen Kerze im Fenster
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
				Description                     = "Opening-Strategie: Richtung aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, Einstieg auf ersten Pullback, Stop an Fensterstruktur, Ziel = 2R.";
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
				OpenHour            = 9;
				OpenMinute          = 0;
				InitialBars         = 5;
				CutoffHour          = 10;
				CutoffMinute        = 0;
				RewardMultiple      = 2;
				StopOffsetTicks     = 2;
				AllowFallbackStop   = true;
				StrictFirstPullback = true;
				Contracts           = 1;
			}
			else if (State == State.DataLoaded)
			{
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
					Log(Name + ": Bitte eine 1-Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + "). Die Logik setzt 1-Min-Kerzen voraus.", LogLevel.Warning);
			}
		}

		private void ResetDay(DateTime day)
		{
			currentDay        = day;
			openPrice         = 0;
			windowBarCount    = 0;
			lastWindowClose   = 0;
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

			// ---------- Phase 1: Beobachtungsfenster (Kerzen 1..InitialBars) ----------
			if (barClose > windowStart && barClose <= windowEnd)
			{
				if (windowBarCount == 0)
					openPrice = Open[0]; // Eroeffnungskurs 09:00

				windowBarCount++;
				lastWindowClose = Close[0];

				if (Close[0] < Open[0]) // rote Kerze
					lowestRedClose = Math.Min(lowestRedClose, Close[0]);
				if (Close[0] > Open[0]) // gruene Kerze
					highestGreenClose = Math.Max(highestGreenClose, Close[0]);

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

			// Cutoff: letzte gueltige Signalkerze schliesst genau zur Cutoff-Zeit
			if (barClose > cutoff)
			{
				entryDone = true;
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// ---------- Phase 3: Einstieg auf den ersten Pullback ----------
			if (bias == 1 && Close[0] < Open[0]) // erste rote Kerze nach dem Fenster
			{
				double basis = lowestRedClose != double.MaxValue ? lowestRedClose
				             : (AllowFallbackStop ? lowestCloseAll : double.MaxValue);
				if (basis == double.MaxValue) { entryDone = true; return; } // keine Stop-Basis vorhanden

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis - StopOffsetTicks * TickSize);
				double risk = Close[0] - stopPrice;

				if (risk < TickSize) // Signalkerze schloss auf/unter Stop-Niveau -> ungueltig
				{
					if (StrictFirstPullback) entryDone = true;
					return;
				}

				SetStopLoss(SignalLong, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalLong, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * risk));
				EnterLong(Contracts, SignalLong);
				entryDone = true;
			}
			else if (bias == -1 && Close[0] > Open[0]) // erste gruene Kerze nach dem Fenster
			{
				double basis = highestGreenClose != double.MinValue ? highestGreenClose
				             : (AllowFallbackStop ? highestCloseAll : double.MinValue);
				if (basis == double.MinValue) { entryDone = true; return; }

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis + StopOffsetTicks * TickSize);
				double risk = stopPrice - Close[0];

				if (risk < TickSize)
				{
					if (StrictFirstPullback) entryDone = true;
					return;
				}

				SetStopLoss(SignalShort, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalShort, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] - RewardMultiple * risk));
				EnterShort(Contracts, SignalShort);
				entryDone = true;
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
		[Range(0.5, 10)]
		[Display(Name = "Reward-Multiple (R)", Description = "Take-Profit = Einstieg +/- Multiple * R (Standard 2)", Order = 10, GroupName = "02 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop-Offset (Ticks)", Description = "Wie weit 'leicht unter/ueber' der Stop-Basis (FDXS: 1 Tick = 1 Punkt)", Order = 11, GroupName = "02 Risiko")]
		public int StopOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Kontrakte", Order = 12, GroupName = "02 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fallback-Stop erlauben", Description = "Keine rote (Long) bzw. gruene (Short) Kerze im Fenster: tiefsten/hoechsten Close aller Fensterkerzen als Stop-Basis nutzen. Sonst kein Trade.", Order = 20, GroupName = "03 Verhalten")]
		public bool AllowFallbackStop { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Nur erste Pullback-Kerze", Description = "True: Ist die erste Pullback-Kerze ungueltig (Close jenseits des Stops), kein Trade an diesem Tag. False: weitere Pullback-Kerzen abwarten.", Order = 21, GroupName = "03 Verhalten")]
		public bool StrictFirstPullback { get; set; }
		#endregion
	}
}
