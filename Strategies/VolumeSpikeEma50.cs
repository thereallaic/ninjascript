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
// VolumeSpikeEma50  —  Volumen-Ausbruch mit EMA50-Filter, long und short
//                      NinjaTrader 8.1, ausgelegt auf 1-Minuten-Kerzen
//
// Regelwerk:
//  1. Handelsfenster 12:00–22:00 (lokale NT-Zeitzone). Ausserhalb passiert nichts.
//     Die Sperre 09:00–12:00 ist ueber den Fensterstart abgebildet, nicht ueber eine
//     eigene Blackout-Mechanik — bei einem Fenster ab 09:00 ist beides identisch.
//     Freitags werden keine neuen Positionen eroeffnet (TradeFriday).
//  2. Signalkerze = Volumen >= VolumeMultiple (Standard 2,0) x Durchschnittsvolumen
//     der VolumeLookback (Standard 20) vorhergehenden Kerzen. Der Durchschnitt
//     schliesst die aktuelle Kerze AUS (SMA[1]), sonst wuerde die Signalkerze ihren
//     eigenen Schwellwert nach oben ziehen.
//  3. LONG  wenn zusaetzlich Close > EMA50.
//     SHORT wenn zusaetzlich Close < EMA50.
//     Market-Order beim Schluss der Signalkerze -> Fill zum Open der Folgekerze.
//  4. Stop = OPEN DER SIGNALKERZE.
//     Daraus folgt implizit die Kerzenfarbe: Bei Long muss Open < Close sein (gruene
//     Kerze), sonst laege der Stop ueber dem Einstieg. Rote Kerzen ueber dem EMA
//     erzeugen daher kein Long-Signal — und umgekehrt fuer Short.
//  5. Positionsgroesse so, dass ein Stopout ungefaehr RiskAmount kostet:
//        Kontrakte = abrunden( RiskAmount / (Stopdistanz x PointValue) )
//     Abgerundet wird bewusst — das Risiko liegt damit nie ueber RiskAmount,
//     bei weiten Stops aber spuerbar darunter.
//  6. KAPPUNGSFALL: Ist die Stopdistanz so gross, dass selbst 1 Kontrakt mehr als
//     RiskAmount riskieren wuerde (Kontrakte = 0), wird 1 Kontrakt gehandelt und der
//     Stop auf genau RiskAmount herangezogen:
//        Stopdistanz = RiskAmount / PointValue
//     Der Stop liegt dann NICHT mehr auf dem Candle-Open, sondern auf dem Geldlimit.
//     Diese Trades werden im Debug-Log als "gekappt" markiert.
//  7. Take-Profit = Einstieg +/- RewardMultiple x tatsaechliche Stopdistanz.
//     RewardMultiple ist der frei einstellbare R-Wert (Standard 1).
//  8. Immer nur eine Position gleichzeitig. Offene Position wird um 22:00
//     glattgestellt (CloseAtWindowEnd).
//
// Stop und Ziel werden nach dem Fill auf den TATSAECHLICHEN Einstiegskurs
// nachgerechnet. Im Normalfall bleibt der Stop dabei auf dem Candle-Open liegen
// (struktureller Level), im Kappungsfall behaelt er den Geldabstand zum Fill.
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class VolumeSpikeEma50 : Strategy
	{
		private const string SignalLong  = "VSE_Long";
		private const string SignalShort = "VSE_Short";

		private EMA      ema;
		private SMA      volAvg;
		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;

		// Zwischenspeicher fuer OnExecutionUpdate
		private double pendingStopLevel;   // absoluter Stop (Candle-Open) im Normalfall
		private double pendingCapDistance; // Geldabstand im Kappungsfall
		private bool   pendingIsCapped;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Volumen-Ausbruch mit EMA50-Filter, long und short: Einstieg auf einer Kerze mit dem 2-fachen Durchschnittsvolumen ober-/unterhalb des EMA50, Stop auf dem Candle-Open, Positionsgroesse aus festem Geldrisiko, frei einstellbares R-Ziel.";
				Name                            = "VolumeSpikeEma50";
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
				BarsRequiredToTrade             = 51;
				IsInstantiatedOnEachOptimizationIteration = true;

				// Parameter-Defaults
				StartHour        = 12;     // 09:00–12:00 ist gesperrt
				StartMinute      = 0;
				EndHour          = 22;
				EndMinute        = 0;
				CloseAtWindowEnd = true;
				TradeFriday      = false;  // freitags keine Einstiege
				EnableLong       = true;
				EnableShort      = true;
				EmaPeriod        = 50;
				VolumeMultiple   = 2.0;
				VolumeLookback   = 20;
				RiskAmount       = 100;
				RewardMultiple   = 1;      // frei einstellbarer R-Wert
				MaxContracts     = 50;
				MinStopTicks     = 10;     // siehe Hinweis unten; 0 = Filter aus
				MaxTradesPerDay  = 0;      // 0 = unbegrenzt
				EnableDebugLog   = false;
			}
			else if (State == State.Configure)
			{
				BarsRequiredToTrade = Math.Max(EmaPeriod, VolumeLookback) + 1;
			}
			else if (State == State.DataLoaded)
			{
				ema    = EMA(EmaPeriod);
				volAvg = SMA(Volume, VolumeLookback);

				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
					Log(Name + ": Bitte eine 1-Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + "). Die Regeln beziehen sich auf 1-Min-Kerzen.", LogLevel.Warning);

				if (Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue des Instruments ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Die Positionsgroesse laesst sich damit nicht berechnen, es werden KEINE Trades ausgefuehrt. "
						+ "Point Value in Control Center -> Tools -> Instruments fuer " + Instrument.FullName + " pruefen (FDXS = 1).", LogLevel.Error);

				if (EnableDebugLog)
					Print(Name + ": Start — Instrument=" + Instrument.FullName
						+ ", PointValue=" + Instrument.MasterInstrument.PointValue
						+ ", TickSize=" + Instrument.MasterInstrument.TickSize
						+ ", RiskAmount=" + RiskAmount + ", RewardMultiple=" + RewardMultiple);
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;
			if (CurrentBar < Math.Max(EmaPeriod, VolumeLookback) + 1)
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
				if (CloseAtWindowEnd && Position.MarketPosition != MarketPosition.Flat)
				{
					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("TimeExit", SignalLong);
					else
						ExitShort("TimeExit", SignalShort);

					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Zeit-Exit @ " + Close[0]);
				}
				return;
			}

			// ---------- Nur innerhalb des Fensters handeln ----------
			// Die Kerze, die exakt zur Startzeit schliesst, enthaelt noch Daten davor
			// und zaehlt deshalb nicht zum Fenster.
			if (barClose <= windowStart)
				return;

			// ---------- Wochentagsfilter ----------
			// Steht bewusst NACH der Glattstellung: sperrt nur neue Einstiege, eine
			// (theoretisch) offene Position wuerde weiterhin korrekt beendet.
			if (!TradeFriday && Time[0].DayOfWeek == DayOfWeek.Friday)
				return;

			// ---------- Volumenbedingung ----------
			double avg = volAvg[1];                       // Durchschnitt OHNE die aktuelle Kerze
			if (avg <= 0)
				return;
			if (Volume[0] < VolumeMultiple * avg)
				return;

			// ---------- Richtung aus dem EMA50 ----------
			bool goLong  = EnableLong  && Close[0] > ema[0];
			bool goShort = EnableShort && Close[0] < ema[0];
			if (!goLong && !goShort)
				return;

			if (Position.MarketPosition != MarketPosition.Flat)
				return;                                    // nur eine Position gleichzeitig

			if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Tageslimit erreicht (" + tradesToday + ")");
				return;
			}

			double pointValue = Instrument.MasterInstrument.PointValue;
			if (pointValue <= 0)
				return;                                    // bereits bei DataLoaded als Fehler geloggt

			// ---------- Stopdistanz aus dem Candle-Open ----------
			double structuralStop = Open[0];               // Open der Signalkerze
			double dist           = goLong ? Close[0] - structuralStop : structuralStop - Close[0];

			// Stop auf der falschen Seite: rote Kerze bei Long bzw. gruene bei Short
			if (dist <= 0)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Stop auf falscher Seite ("
						+ (goLong ? "rote" : "gruene") + " Kerze, Open=" + Open[0] + ", Close=" + Close[0] + ")");
				return;
			}

			if (dist < Math.Max(1, MinStopTicks) * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Stopdistanz " + dist + " unter Mindestwert");
				return;
			}

			// ---------- Positionsgroesse ----------
			int    qty       = (int)Math.Floor(RiskAmount / (dist * pointValue));
			double useDist   = dist;
			bool   isCapped  = false;

			if (qty < 1)
			{
				// Selbst 1 Kontrakt riskiert mehr als RiskAmount -> Stop auf das Geldlimit ziehen
				qty      = 1;
				useDist  = RiskAmount / pointValue;
				isCapped = true;
			}

			qty = Math.Min(qty, MaxContracts);
			if (qty < 1)
				return;

			double stopLevel = goLong
				? Instrument.MasterInstrument.RoundToTickSize(Close[0] - useDist)
				: Instrument.MasterInstrument.RoundToTickSize(Close[0] + useDist);

			double targetLevel = goLong
				? Instrument.MasterInstrument.RoundToTickSize(Close[0] + RewardMultiple * useDist)
				: Instrument.MasterInstrument.RoundToTickSize(Close[0] - RewardMultiple * useDist);

			// Fuer die Nachrechnung nach dem Fill merken
			pendingIsCapped    = isCapped;
			pendingCapDistance = useDist;
			pendingStopLevel   = isCapped ? 0 : Instrument.MasterInstrument.RoundToTickSize(structuralStop);

			// ---------- Einstieg ----------
			if (goLong)
			{
				SetStopLoss(SignalLong, CalculationMode.Price, stopLevel, false);
				SetProfitTarget(SignalLong, CalculationMode.Price, targetLevel);
				EnterLong(qty, SignalLong);
			}
			else
			{
				SetStopLoss(SignalShort, CalculationMode.Price, stopLevel, false);
				SetProfitTarget(SignalShort, CalculationMode.Price, targetLevel);
				EnterShort(qty, SignalShort);
			}
			tradesToday++;

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: " + (goLong ? "LONG " : "SHORT ") + qty + " @ " + Close[0]
					+ " | Vol=" + Volume[0] + " (" + Math.Round(Volume[0] / avg, 2) + "x Schnitt " + Math.Round(avg, 1) + ")"
					+ " | EMA50=" + Math.Round(ema[0], 1)
					+ " | Stop=" + stopLevel + " (" + Math.Round(useDist, 1) + " Pkt" + (isCapped ? ", GEKAPPT" : ", Candle-Open") + ")"
					+ " | Risiko=" + Math.Round(useDist * pointValue * qty, 2)
					+ " | Ziel=" + targetLevel);
		}

		// Stop und Ziel auf den tatsaechlichen Fill nachrechnen.
		// Normalfall: Stop bleibt auf dem Candle-Open (struktureller Level).
		// Kappungsfall: Stop behaelt den Geldabstand zum Fill, damit das Risiko exakt stimmt.
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

			double stop;
			if (pendingIsCapped)
				stop = isLong ? price - pendingCapDistance : price + pendingCapDistance;
			else
				stop = pendingStopLevel;

			stop = Instrument.MasterInstrument.RoundToTickSize(stop);

			double risk = isLong ? price - stop : stop - price;
			if (risk < TickSize)
				return;                                    // Sicherheitsnetz, Level bleibt wie gesetzt

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price + RewardMultiple * risk : price - RewardMultiple * risk);

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
				Print(time.ToString("yyyy-MM-dd HH:mm") + " VSE: Fill @ " + price + " x" + quantity
					+ " | Stop=" + stop + " | Ziel=" + target
					+ " | tatsaechliches Risiko=" + Math.Round(risk * Instrument.MasterInstrument.PointValue * quantity, 2));
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
		[Display(Name = "Zum Fensterende glattstellen", Description = "True (Standard): offene Position wird am Fensterende geschlossen. False: Position laeuft bis Stop oder Ziel (bzw. Sessionende).", Order = 5, GroupName = "01 Zeiten")]
		public bool CloseAtWindowEnd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Freitags handeln", Description = "False (Standard): freitags werden keine neuen Positionen eroeffnet. Eine bereits offene Position wird trotzdem regulaer beendet.", Order = 6, GroupName = "01 Zeiten")]
		public bool TradeFriday { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "EMA-Periode", Description = "Periode des EMA-Richtungsfilters (Standard 50).", Order = 10, GroupName = "02 Signal")]
		public int EmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 20.0)]
		[Display(Name = "Volumen-Faktor", Description = "Ab dem Wievielfachen des Durchschnittsvolumens eine Kerze als Ausbruch gilt (Standard 2,0).", Order = 11, GroupName = "02 Signal")]
		public double VolumeMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Volumen-Durchschnitt (Kerzen)", Description = "Ueber wie viele vorhergehende Kerzen der Durchschnitt gebildet wird. Die aktuelle Kerze zaehlt nicht mit (Standard 20).", Order = 12, GroupName = "02 Signal")]
		public int VolumeLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben", Description = "Einstiege oberhalb des EMA50 zulassen. Zum Isolieren einer Richtung abschaltbar.", Order = 13, GroupName = "02 Signal")]
		public bool EnableLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Description = "Einstiege unterhalb des EMA50 zulassen.", Order = 14, GroupName = "02 Signal")]
		public bool EnableShort { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade (1R)", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den ein Stopout kostet. FDXS rechnet in EUR -> 100 = 100 EUR.", Order = 20, GroupName = "03 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Take-Profit = Einstieg +/- Multiple x Stopdistanz. Standard 1. Das ist der Wert zum Durchtesten verschiedener R-Ziele.", Order = 21, GroupName = "03 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse. Greift bei sehr engen Stops.", Order = 22, GroupName = "03 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Stopdistanz (Ticks)", Description = "Signale mit kleinerer Stopdistanz verwerfen. Wichtig, weil die Positionsgroesse invers zur Stopdistanz waechst: Ein 5-Punkte-Stop bedeutet 20 Kontrakte und damit den 20-fachen Kostenblock bei gleichem 1R. Standard 10. 0 = Filter aus.", Order = 23, GroupName = "03 Risiko")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 200)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt. Es ist ohnehin immer nur eine Position gleichzeitig offen.", Order = 30, GroupName = "04 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jedes Signal (inkl. Volumenverhaeltnis, EMA, Stopdistanz, Risiko und Kappung), jeden Fill und jeden Zeit-Exit ins NinjaScript Output-Fenster.", Order = 40, GroupName = "05 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
