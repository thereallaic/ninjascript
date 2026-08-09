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
// OpeningImmediate2R  —  Variante 2 von OpeningPullback2R, NinjaTrader 8.1
//
// Unterschied zu OpeningPullback2R: KEIN Warten auf eine rote/gruene Pullback-Kerze.
// Der Einstieg erfolgt sofort nach dem Richtungsentscheid.
//
// Regelwerk:
//  1. Referenzkurs = Open der 1-Minuten-Kerze um 09:00 (Xetra-Eroeffnung).
//  2. Die ersten 5 Kerzen (09:00–09:05) werden nur beobachtet.
//     Schlusskurs der 5. Kerze > Referenz  -> Long-Einstieg
//     Schlusskurs der 5. Kerze < Referenz  -> Short-Einstieg
//  3. Einstieg per Market direkt nach Schluss der Entscheidungskerze.
//     EntryDelayBars = 0 (Standard): Order beim Schluss der 5. Kerze -> Fill 09:05:00.
//     EntryDelayBars = 1:            eine Kerze spaeter              -> Fill 09:06:00.
//  4. Stop (Long)  = Close der ZULETZT gesehenen ROTEN   Kerze im Fenster − Offset.
//     Stop (Short) = Close der ZULETZT gesehenen GRUENEN Kerze im Fenster + Offset.
//     R = Distanz Einstieg->Stop.  Take-Profit = Einstieg +/- RewardMultiple * R.
//  5. Maximal 1 Trade pro Tag.
//
// Wichtig: 1-Minuten-Datenserie verwenden. Zeitzone Berlin in NT einstellen
// (Tools > Options > General > Time zone), sonst trifft die 9:00-Logik nicht.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class OpeningImmediate2R : Strategy
	{
		private const string SignalLong  = "OI2R_Long";
		private const string SignalShort = "OI2R_Short";

		private DateTime currentDay = DateTime.MinValue;
		private double   openPrice;          // Open der 09:00-Kerze
		private int      windowBarCount;     // Anzahl verarbeiteter Anfangskerzen
		private double   lastWindowClose;    // Close der letzten Kerze im Fenster
		private double   lastRedClose;       // Close der zuletzt gesehenen ROTEN Kerze im Fenster
		private double   lastGreenClose;     // Close der zuletzt gesehenen GRUENEN Kerze im Fenster
		private double   lowestRedClose;     // optionale Alt-Basis: tiefster roter Close im Fenster
		private double   highestGreenClose;  // optionale Alt-Basis: hoechster gruener Close im Fenster
		private double   lowestCloseAll;     // Fallback: tiefster Close aller Fensterkerzen
		private double   highestCloseAll;    // Fallback: hoechster Close aller Fensterkerzen
		private int      bias;               // +1 Long, -1 Short, 0 kein Trade
		private bool     biasDecided;
		private int      barsSinceDecision;  // fuer EntryDelayBars
		private bool     entryDone;          // heute wurde eingestiegen bzw. der Tag ist abgehakt
		private double   stopPrice;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Opening-Strategie Variante 2: Richtung aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, SOFORTIGER Einstieg nach der Entscheidungskerze (kein Pullback), Stop an Fensterstruktur, Ziel = 2R.";
				Name                            = "OpeningImmediate2R";
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
				OpenHour             = 9;
				OpenMinute           = 0;
				InitialBars          = 5;
				EntryDelayBars       = 0;
				RewardMultiple       = 2;
				StopOffsetTicks      = 2;
				UseWindowExtremeStop = false;
				MinRiskTicks         = 0;
				AllowFallbackStop    = true;
				UseFixedRisk         = true;
				RiskPerTrade         = 100;
				MaxContracts         = 50;
				Contracts            = 1;
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
			barsSinceDecision = 0;
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

			if (entryDone)
				return;

			TimeSpan barClose    = Time[0].TimeOfDay;                                   // NT8: Zeitstempel = Kerzenschluss
			TimeSpan windowStart = new TimeSpan(OpenHour, OpenMinute, 0);               // 09:00:00
			TimeSpan windowEnd   = windowStart + TimeSpan.FromMinutes(InitialBars);     // 09:05:00

			// ---------- Phase 1: Beobachtungsfenster (Kerzen 1..InitialBars) ----------
			if (barClose > windowStart && barClose <= windowEnd)
			{
				if (windowBarCount == 0)
					openPrice = Open[0]; // Eroeffnungskurs 09:00

				windowBarCount++;
				lastWindowClose = Close[0];

				if (Close[0] < Open[0]) // rote Kerze
				{
					lastRedClose   = Close[0];
					lowestRedClose = Math.Min(lowestRedClose, Close[0]);
				}
				if (Close[0] > Open[0]) // gruene Kerze
				{
					lastGreenClose    = Close[0];
					highestGreenClose = Math.Max(highestGreenClose, Close[0]);
				}

				lowestCloseAll  = Math.Min(lowestCloseAll, Close[0]);
				highestCloseAll = Math.Max(highestCloseAll, Close[0]);

				// Entscheid direkt beim Schluss der letzten Fensterkerze
				if (windowBarCount == InitialBars)
					DecideBias();
				else
					return;
			}
			// Fallback bei Datenluecke: letzte Fensterkerze fehlt -> auf der ersten
			// Kerze nach dem Fenster entscheiden
			else if (!biasDecided && barClose > windowEnd)
			{
				DecideBias();
			}

			if (entryDone || bias == 0 || !biasDecided)
				return;

			// ---------- Phase 2: Einstieg (sofort oder nach EntryDelayBars Kerzen) ----------
			if (barsSinceDecision < EntryDelayBars)
			{
				barsSinceDecision++;
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			if (bias == 1)
			{
				double basis = UseWindowExtremeStop ? lowestRedClose : lastRedClose;
				if (basis == double.MaxValue)
					basis = AllowFallbackStop ? lowestCloseAll : double.MaxValue;
				if (basis == double.MaxValue) { entryDone = true; return; } // keine Stop-Basis vorhanden

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis - StopOffsetTicks * TickSize);
				double risk = Close[0] - stopPrice;

				if (risk < Math.Max(1, MinRiskTicks) * TickSize) { entryDone = true; return; } // zu klein / unter Mindest-R

				int qtyLong = CalcQuantity(risk);
				if (qtyLong < 1) { entryDone = true; return; } // R zu gross fuer das Geldrisiko

				SetStopLoss(SignalLong, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalLong, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * risk));
				EnterLong(qtyLong, SignalLong);
				entryDone = true;
			}
			else if (bias == -1)
			{
				double basis = UseWindowExtremeStop ? highestGreenClose : lastGreenClose;
				if (basis == double.MinValue)
					basis = AllowFallbackStop ? highestCloseAll : double.MinValue;
				if (basis == double.MinValue) { entryDone = true; return; }

				stopPrice   = Instrument.MasterInstrument.RoundToTickSize(basis + StopOffsetTicks * TickSize);
				double risk = stopPrice - Close[0];

				if (risk < Math.Max(1, MinRiskTicks) * TickSize) { entryDone = true; return; }

				int qtyShort = CalcQuantity(risk);
				if (qtyShort < 1) { entryDone = true; return; }

				SetStopLoss(SignalShort, CalculationMode.Price, stopPrice, false);
				SetProfitTarget(SignalShort, CalculationMode.Price,
					Instrument.MasterInstrument.RoundToTickSize(Close[0] - RewardMultiple * risk));
				EnterShort(qtyShort, SignalShort);
				entryDone = true;
			}
		}

		private void DecideBias()
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
		[Range(0, 30)]
		[Display(Name = "Einstiegsverzoegerung (Kerzen)", Description = "0 = Order beim Schluss der 5. Kerze (Fill 09:05:00). 1 = eine Kerze spaeter (Fill 09:06:00).", Order = 4, GroupName = "01 Zeiten")]
		public int EntryDelayBars { get; set; }

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
		[Display(Name = "Stop aus Fenster-Extrem", Description = "False (Standard): Stop = Close der zuletzt gesehenen Gegenkerze im Fenster. True: tiefster roter / hoechster gruener Close der 5 Anfangskerzen (deutlich weitere Stops).", Order = 16, GroupName = "02 Risiko")]
		public bool UseWindowExtremeStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Risiko (Ticks)", Description = "Ist der Abstand Einstieg->Stop kleiner als dieser Wert, wird der Trade verworfen (Stop laege im Rauschen). 0 = Filter aus.", Order = 17, GroupName = "02 Risiko")]
		public int MinRiskTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Fallback-Stop erlauben", Description = "Keine Gegenkerze im Fenster vorhanden: tiefsten/hoechsten Close aller Fensterkerzen als Stop-Basis nutzen. Sonst kein Trade.", Order = 20, GroupName = "03 Verhalten")]
		public bool AllowFallbackStop { get; set; }
		#endregion
	}
}
