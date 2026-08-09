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
// VolumeSpikeLong1R  —  Volumen-Ausbruch Long-only, NinjaTrader 8.1
//
// Regelwerk:
//  1. Handelsfenster 10:00–15:00 (lokale NT-Zeitzone). Ausserhalb passiert nichts.
//  2. NUR LONG.
//  3. Einstieg, wenn eine Kerze BEIDE Bedingungen erfuellt:
//       a) Volumen >= VolumeMultiple (Standard 1,5) x Durchschnittsvolumen
//          der letzten VolumeLookback Kerzen. Der Durchschnitt schliesst die
//          aktuelle Kerze AUS (SMA[1]), sonst wuerde die Signalkerze ihren
//          eigenen Schwellwert nach oben ziehen.
//       b) Gruene Kerze (Close > Open).
//     Market-Order beim Schluss der Signalkerze -> Fill zum Open der Folgekerze.
//  4. Festes Geldrisiko: Stop und Ziel liegen so, dass Verlust bzw. Gewinn genau
//     RiskAmount (Standard 100) in Instrumentenwaehrung betragen.
//       Stopdistanz = RiskAmount / (PointValue x Kontrakte)
//       Bei FDXS (1 Punkt = 1 EUR) und 1 Kontrakt sind das 100 Punkte.
//     Stop und Ziel werden nach dem Fill auf den TATSAECHLICHEN Einstiegskurs
//     nachgerechnet, damit das Risiko exakt stimmt.
//  5. Immer nur eine Position gleichzeitig. Neue Signale waehrend einer offenen
//     Position werden ignoriert.
//  6. Offene Position wird um 15:00 glattgestellt (CloseAtWindowEnd).
//
// Achtung zur Interpretation: Durch den Zeit-Exit gibt es DREI Ausgaenge, nicht zwei —
// +1R, −1R und "Zeit-Exit irgendwo dazwischen". Je weiter RiskAmount, desto haeufiger
// der dritte Fall. Die Auswertung im Trades-Tab entsprechend lesen.
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class VolumeSpikeLong1R : Strategy
	{
		private const string SignalLong = "VSL_Long";

		private SMA      volAvg;
		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Long-only Volumen-Ausbruch zwischen 10:00 und 15:00: Einstieg auf einer gruenen Kerze mit dem 1,5-fachen Durchschnittsvolumen, festes Geldrisiko mit 1R-Ziel.";
				Name                            = "VolumeSpikeLong1R";
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
				BarsRequiredToTrade             = 21;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Parameter-Defaults
				StartHour        = 10;
				StartMinute      = 0;
				EndHour          = 15;
				EndMinute        = 0;
				CloseAtWindowEnd = true;
				VolumeMultiple   = 1.5;
				VolumeLookback   = 20;
				RiskAmount       = 100;
				RewardMultiple   = 1;
				Contracts        = 1;
				MaxTradesPerDay  = 0;      // 0 = unbegrenzt
				EnableDebugLog   = false;
			}
			else if (State == State.Configure)
			{
				// Genug Vorlauf fuer den Volumen-Durchschnitt sicherstellen
				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, VolumeLookback + 1);
			}
			else if (State == State.DataLoaded)
			{
				volAvg = SMA(Volume, VolumeLookback);

				if (Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue des Instruments ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Die Stopdistanz laesst sich damit nicht berechnen, es werden KEINE Trades ausgefuehrt. "
						+ "Point Value in Control Center -> Tools -> Instruments fuer " + Instrument.FullName + " pruefen (FDXS = 1).", LogLevel.Error);

				if (EnableDebugLog)
					Print(Name + ": Start — Instrument=" + Instrument.FullName
						+ ", PointValue=" + Instrument.MasterInstrument.PointValue
						+ ", TickSize=" + Instrument.MasterInstrument.TickSize
						+ ", Stopdistanz=" + StopDistance() + " Punkte");
			}
		}

		// Kursdistanz, die bei der eingestellten Kontraktzahl genau RiskAmount kostet.
		private double StopDistance()
		{
			double pointValue = Instrument.MasterInstrument.PointValue;
			if (pointValue <= 0 || Contracts < 1)
				return 0;
			return RiskAmount / (pointValue * Contracts);
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;
			if (CurrentBar < VolumeLookback + 1)
				return;

			// Neuer Handelstag -> Zaehler zuruecksetzen
			if (Time[0].Date != currentDay)
			{
				currentDay  = Time[0].Date;
				tradesToday = 0;
			}

			TimeSpan barClose    = Time[0].TimeOfDay;                        // NT8: Zeitstempel = Kerzenschluss
			TimeSpan windowStart = new TimeSpan(StartHour, StartMinute, 0);
			TimeSpan windowEnd   = new TimeSpan(EndHour,   EndMinute,   0);

			// ---------- Fensterende: offene Position glattstellen ----------
			if (barClose >= windowEnd)
			{
				if (CloseAtWindowEnd && Position.MarketPosition == MarketPosition.Long)
				{
					ExitLong(Contracts, "TimeExit", SignalLong);
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSL: Zeit-Exit @ " + Close[0]);
				}
				return;
			}

			// ---------- Nur innerhalb 10:00–15:00 handeln ----------
			// barClose > windowStart: die Kerze, die um 10:00 schliesst, enthaelt noch
			// Daten von vor 10:00 und zaehlt daher nicht zum Fenster.
			if (barClose <= windowStart)
				return;

			// ---------- Signalpruefung ----------
			double avg = volAvg[1];                       // Durchschnitt OHNE die aktuelle Kerze
			if (avg <= 0)
				return;

			bool volumeSpike = Volume[0] >= VolumeMultiple * avg;
			bool isGreen     = Close[0] > Open[0];

			if (!volumeSpike || !isGreen)
				return;

			// ---------- Filter vor dem Einstieg ----------
			if (Position.MarketPosition != MarketPosition.Flat)
				return;                                    // nur eine Position gleichzeitig

			if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSL: Signal verworfen — Tageslimit erreicht (" + tradesToday + ")");
				return;
			}

			double dist = StopDistance();
			if (dist < TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSL: Signal verworfen — Stopdistanz " + dist + " (PointValue=" + Instrument.MasterInstrument.PointValue + ")");
				return;
			}

			// ---------- Einstieg ----------
			// Vorlaeufige Level auf Basis des Schlusskurses; nach dem Fill exakt nachgerechnet.
			SetStopLoss(SignalLong, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(Close[0] - dist), false);
			SetProfitTarget(SignalLong, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * dist));
			EnterLong(Contracts, SignalLong);
			tradesToday++;

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSL: LONG " + Contracts + " @ " + Close[0]
					+ " | Vol=" + Volume[0] + " (" + Math.Round(Volume[0] / avg, 2) + "x Schnitt " + Math.Round(avg, 1) + ")"
					+ " | Stopdistanz=" + dist + " Punkte | Trade " + tradesToday + " heute");
		}

		// Stop und Ziel exakt auf den tatsaechlichen Fill nachrechnen, damit das
		// Geldrisiko unabhaengig vom Slippage zwischen Signalkerze und Fill stimmt.
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
			int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null)
				return;
			if (execution.Order.OrderState != OrderState.Filled && execution.Order.OrderState != OrderState.PartFilled)
				return;
			if (execution.Order.Name != SignalLong)
				return;

			double dist = StopDistance();
			if (dist < TickSize)
				return;

			SetStopLoss(SignalLong, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(price - dist), false);
			SetProfitTarget(SignalLong, CalculationMode.Price,
				Instrument.MasterInstrument.RoundToTickSize(price + RewardMultiple * dist));

			if (EnableDebugLog)
				Print(time.ToString("yyyy-MM-dd HH:mm") + " VSL: Fill @ " + price
					+ " | Stop=" + Instrument.MasterInstrument.RoundToTickSize(price - dist)
					+ " | Ziel=" + Instrument.MasterInstrument.RoundToTickSize(price + RewardMultiple * dist));
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
		[Display(Name = "Ende – Stunde", Description = "Ende des Handelsfensters. Danach keine neuen Einstiege.", Order = 3, GroupName = "01 Zeiten")]
		public int EndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Ende – Minute", Order = 4, GroupName = "01 Zeiten")]
		public int EndMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Zum Fensterende glattstellen", Description = "True (Standard): offene Position wird am Fensterende geschlossen. False: Position laeuft weiter bis Stop oder Ziel (bzw. Sessionende).", Order = 5, GroupName = "01 Zeiten")]
		public bool CloseAtWindowEnd { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 20.0)]
		[Display(Name = "Volumen-Faktor", Description = "Ab dem Wievielfachen des Durchschnittsvolumens eine Kerze als Ausbruch gilt (Standard 1,5).", Order = 10, GroupName = "02 Signal")]
		public double VolumeMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Volumen-Durchschnitt (Kerzen)", Description = "Ueber wie viele vorhergehende Kerzen der Durchschnitt gebildet wird. Die aktuelle Kerze zaehlt nicht mit.", Order = 11, GroupName = "02 Signal")]
		public int VolumeLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade (1R)", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den ein Stopout kostet. FDXS rechnet in EUR -> 100 = 100 EUR = 100 Punkte bei 1 Kontrakt.", Order = 20, GroupName = "03 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 10)]
		[Display(Name = "Reward-Multiple (R)", Description = "Take-Profit = Einstieg + Multiple * R (Standard 1).", Order = 21, GroupName = "03 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Kontrakte", Description = "Feste Positionsgroesse. Die Stopdistanz wird so gewaehlt, dass das Risiko trotzdem genau dem Betrag oben entspricht.", Order = 22, GroupName = "03 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt. Es ist ohnehin immer nur eine Position gleichzeitig offen.", Order = 30, GroupName = "04 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jedes Signal, jeden Fill und jeden Zeit-Exit ins NinjaScript Output-Fenster.", Order = 40, GroupName = "05 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
