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
//     WOCHENTAGE einzeln schaltbar (TradeMonday ... TradeFriday). Standard: NUR
//     MONTAG aktiv — die Wochentagsauswertung des Backtests (Lifetime-Kommission,
//     2020–2026) zeigte Montag mit +33.544 $ als einzigen klar profitablen Tag
//     (Mittwoch −16.718 $, Di/Do ~0). ACHTUNG Curve-Fitting-Risiko: Die Auswahl
//     stammt aus denselben Daten — vor Live-Einsatz auf Out-of-Sample validieren.
//     Die Schalter sperren nur NEUE Einstiege; eine offene Position wird an jedem
//     Tag regulaer verwaltet und beendet. Wochenend-Sessions (Sonntag) sind
//     grundsaetzlich gesperrt.
//  2. Signalkerze = Volumen >= VolumeMultiple (Standard 2,0) x Durchschnittsvolumen
//     der VolumeLookback (Standard 20) vorhergehenden Kerzen. Der Durchschnitt
//     schliesst die aktuelle Kerze AUS (SMA[1]), sonst wuerde die Signalkerze ihren
//     eigenen Schwellwert nach oben ziehen.
//  3. LONG  wenn zusaetzlich Close > EMA50.
//     SHORT wenn zusaetzlich Close < EMA50.
//     Market-Order beim Schluss der Signalkerze -> Fill zum Open der Folgekerze.
//  3b. EMA-DURCHBRUCH-FILTER (UseEmaCrossFilter, Standard an): Quert die Signalkerze
//     den EMA — Close auf der Handelsseite, Open noch jenseits —, wird sie verworfen.
//     Der Open muss bereits auf derselben Seite liegen wie der Close.
//  3c. KERZENFORM-FILTER (UseCandleShapeFilter, Standard an): Ist der Ablehnungsdocht
//     laenger als MaxWickToBodyRatio (Standard 2,0) mal der Kerzenkoerper, wird das
//     Signal verworfen. Ablehnungsdocht = der Docht GEGEN die Handelsrichtung:
//     bei Long oben (High - Close), bei Short unten (Close - Low).
//  4. Stop = OPEN DER SIGNALKERZE (struktureller Stop).
//     Daraus folgt implizit die Kerzenfarbe: Bei Long muss Open < Close sein (gruene
//     Kerze), sonst laege der Stop ueber dem Einstieg. Rote Kerzen ueber dem EMA
//     erzeugen daher kein Long-Signal — und umgekehrt fuer Short.
//  4b. STOP-DECKEL (MaxRiskPerContract, Standard 12,50, 0 = aus):
//     MAE-Auswertung des Backtests (MNQ 2020–2026): Gewinner hatten eine Median-MAE
//     von 6,50 je Kontrakt und blieben zu 90 % unter 26,50, waehrend der strukturelle
//     Stop im Schnitt 23,50 je Kontrakt kostete. Die Gewinner nutzen die Stopdistanz
//     also kaum. Deshalb wird die Stopdistanz auf hoechstens MaxRiskPerContract
//     (Geld je Kontrakt, Instrumentenwaehrung) gedeckelt:
//        Stopdistanz = min( Close - Open , MaxRiskPerContract / PointValue )
//     Greift der Deckel, liegt der Stop NICHT mehr auf dem Candle-Open, sondern auf
//     dem Geldlimit — analog zum Kappungsfall (Regel 6), nur je Kontrakt statt je
//     Position. Der Standard 12,50 ist das Simulationsoptimum aus dem Backtest und
//     damit curve-fitting-anfaellig; das 90. Perzentil der Gewinner-MAE (~26,50) ist
//     die konservative Alternative. Der Wert ist als Property optimierbar.
//     WICHTIG: Das TAKE-PROFIT-ZIEL wird weiterhin aus der STRUKTURELLEN Distanz
//     (Candle-Open) gerechnet, nicht aus der gedeckelten. Sonst wuerde der Deckel
//     auch die Gewinne stauchen — die MAE-Erkenntnis sagt nur, dass Gewinner wenig
//     Gegenbewegung sehen, nicht, dass sie frueher exiten sollen. Das effektive
//     Chance-Risiko-Verhaeltnis steigt dadurch ueber RewardMultiple hinaus.
//  5. Positionsgroesse (RoundContractsUp, Standard an — AUFRUNDEN AUF EXAKTES 1R):
//        Kontrakte   = aufrunden( RiskAmount / (Stopdistanz x PointValue) )
//        Stopdistanz = min( Stopdistanz , RiskAmount / (Kontrakte x PointValue) )
//     Statt abzurunden (und damit im Schnitt nur ~85–90 % von 1R zu riskieren) wird
//     die Kontraktzahl AUFGERUNDET und der Stop anschliessend exakt auf das
//     1R-Budget nachgezogen. Beispiel MNQ, RiskAmount 100, Distanz 9 je Kontrakt:
//     abrunden ergaebe 11 Kontrakte / 99 Risiko — aufrunden ergibt 12 Kontrakte,
//     Stop nachgezogen auf 8,33 je Kontrakt, Risiko exakt 100. Ein Stopout kostet
//     damit IMMER genau RiskAmount (bis auf Tick-Rundung), die Kontraktzahl ist
//     maximal. Der nachgezogene Stop ist dann ein Geldabstand zum Fill, kein
//     struktureller Level mehr. Konsequenz wie beim Deckel: mehr Kontrakte =
//     mehr Kommission je Trade.
//     Der alte KAPPUNGSFALL (Distanz so gross, dass 1 Kontrakt mehr als RiskAmount
//     riskiert) ist in diesem Modus automatisch abgedeckt: aufrunden ergibt 1
//     Kontrakt, der Fit zieht den Stop auf RiskAmount / PointValue heran.
//  5b. LEGACY-MODUS (RoundContractsUp = false): Verhalten wie bisher —
//        Kontrakte = abrunden( RiskAmount / (Stopdistanz x PointValue) )
//     Das Risiko liegt damit nie ueber RiskAmount, bei weiten Stops aber spuerbar
//     darunter. Der Stop bleibt (ausser im Deckel-/Kappungsfall) auf dem Candle-Open.
//  6. KAPPUNGSFALL (nur im Legacy-Modus relevant): Ist die Stopdistanz so gross,
//     dass selbst 1 Kontrakt mehr als RiskAmount riskieren wuerde (Kontrakte = 0),
//     wird 1 Kontrakt gehandelt und der Stop auf genau RiskAmount herangezogen:
//        Stopdistanz = RiskAmount / PointValue
//     Der Stop liegt dann NICHT mehr auf dem Candle-Open, sondern auf dem Geldlimit.
//     In diesem Fall rechnet auch das Ziel aus der gekappten Distanz (wie bisher).
//     Bei aktivem Stop-Deckel <= RiskAmount kann dieser Fall praktisch nicht mehr
//     eintreten; der Zweig bleibt als Sicherheitsnetz erhalten.
//     Diese Trades werden im Debug-Log als "gekappt" markiert.
//  7. Take-Profit = Einstieg +/- RewardMultiple x strukturelle Stopdistanz
//     (Candle-Open). RewardMultiple ist der frei einstellbare R-Wert (Standard 1).
//     Das gilt auch, wenn der Stop durch Deckel oder 1R-Fit nachgezogen wurde —
//     das Ziel bleibt strukturell, das effektive CRV steigt entsprechend.
//     Einzige Ausnahme: der Legacy-Kappungsfall (Regel 6) rechnet das Ziel wie
//     bisher aus der Geld-Distanz.
//  7b. BREAK-EVEN-MOVE (UseBreakEven, Standard AUS):
//     MFE-Auswertung des Backtests: 35,6 % der Verlierer standen zwischenzeitlich
//     mindestens 1x Stopdistanz im Plus, bevor sie in den Stop liefen (ETD der
//     Verlierer im Schnitt 28,70 je Kontrakt). Deshalb wird der Stop auf Einstieg
//     (+ BreakEvenOffsetTicks) gezogen, sobald der Kurs
//        BreakEvenTriggerMultiple x finale Stopdistanz
//     im Gewinn stand. Referenz ist die FINALE Stopdistanz (nach Deckel und
//     1R-Fit) — der Trigger ist also ein Vielfaches des tatsaechlichen 1R.
//     Mechanik bei Calculate.OnBarClose: geprueft wird am Kerzenschluss gegen
//     High/Low der abgeschlossenen Kerze. Hat dieselbe Kerze den Trigger erreicht
//     UND schliesst schon wieder jenseits des BE-Levels, wird zum Close per Market
//     geschlossen (Exit-Name "BEExit") — konservativ gebucht, denn intrabar waere
//     der Trade am BE-Level (besser) ausgestoppt worden. Der BE-Stop wird nur in
//     Gewinnrichtung gezogen, nie zurueck. Fuer belastbare Intrabar-Sequenzen
//     gilt weiterhin: High Fill Resolution / Tick Replay verwenden.
//     ACHTUNG: Der Move ist Fluch und Segen zugleich — er neutralisiert Verlierer,
//     stoppt aber auch spaetere 7R-Gewinner auf null aus, die nach dem Trigger
//     nochmal zum Einstieg zurueckkommen. Der Nettoeffekt ist empirisch zu testen
//     (Trigger im Optimizer durchfahren), nicht theoretisch entscheidbar.
//     EMPIRIE-STAND 08/2026: In den Tick-Backtests war der BE-Move in keiner
//     Konfiguration besser als ohne — deshalb Standard AUS. Der Code bleibt fuer
//     erneute Tests (z. B. nach Filteraenderungen) erhalten.
//  8. Immer nur eine Position gleichzeitig. Offene Position wird um 22:00
//     glattgestellt (CloseAtWindowEnd).
//
// Stop und Ziel werden nach dem Fill auf den TATSAECHLICHEN Einstiegskurs
// nachgerechnet. Normalfall: Stop bleibt auf dem Candle-Open (struktureller Level).
// Deckel- und Kappungsfall: Stop behaelt den Geldabstand zum Fill. Das Ziel behaelt
// immer seine Distanz (strukturell bzw. im Kappungsfall die Geld-Distanz) zum Fill.
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
		private double pendingStopLevel;    // absoluter Stop (Candle-Open) im strukturellen Fall
		private double pendingStopDistance; // Stopdistanz in Punkten, wenn der Stop ein Geldabstand ist
		private double pendingTargetDist;   // Zieldistanz in Punkten (immer relativ zum Fill)
		private bool   pendingStopIsMoney;  // true = Deckel- oder Kappungsfall: Stop haengt am Fill

		// Break-Even-Management (Regel 7b)
		private double activeStopDistance;  // finale Stopdistanz der offenen Position (Punkte)
		private bool   beApplied;           // BE-Stop fuer diese Position bereits gesetzt

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Volumen-Ausbruch mit EMA50-Filter, long und short: Einstieg auf einer Kerze mit dem 2-fachen Durchschnittsvolumen ober-/unterhalb des EMA50, Stop auf dem Candle-Open mit Geld-Deckel je Kontrakt (aus der MAE-Auswertung), Positionsgroesse aus festem Geldrisiko, frei einstellbares R-Ziel auf Basis der strukturellen Distanz.";
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
				TradeMonday      = true;   // Wochentagsauswertung: Montag einziger klar profitabler Tag
				TradeTuesday     = false;
				TradeWednesday   = false;
				TradeThursday    = false;
				TradeFriday      = false;
				EnableLong       = true;
				EnableShort      = true;
				EmaPeriod        = 50;
				VolumeMultiple   = 2.0;
				VolumeLookback   = 20;
				UseCandleShapeFilter = true;
				MaxWickToBodyRatio   = 2.0;  // Docht > 2 x Body -> kein Trade
				UseEmaCrossFilter    = true; // Kerzen, die den EMA queren, ausschliessen
				RiskAmount       = 100;
				RewardMultiple   = 1;      // frei einstellbarer R-Wert
				MaxRiskPerContract = 12.5; // Stop-Deckel je Kontrakt aus der MAE-Auswertung; 0 = aus
				RoundContractsUp = true;   // Kontrakte aufrunden, Stop exakt auf 1R nachziehen
				UseBreakEven     = false;  // Stand 08/2026: im Tick-Backtest nie besser als ohne — Code bleibt fuer Retests
				BreakEvenTriggerMultiple = 1.0; // Trigger in Vielfachen der finalen Stopdistanz
				BreakEvenOffsetTicks     = 0;   // 0 = exakt Einstieg; positiv = Kostenpuffer
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

				if (MaxRiskPerContract > 0 && MaxRiskPerContract > RiskAmount)
					Log(Name + ": MaxRiskPerContract (" + MaxRiskPerContract + ") liegt ueber RiskAmount (" + RiskAmount
						+ "). Der Deckel greift dann nur bei sehr weiten Stops — vermutlich nicht beabsichtigt.", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — Instrument=" + Instrument.FullName
						+ ", PointValue=" + Instrument.MasterInstrument.PointValue
						+ ", TickSize=" + Instrument.MasterInstrument.TickSize
						+ ", RiskAmount=" + RiskAmount + ", RewardMultiple=" + RewardMultiple
						+ ", MaxRiskPerContract=" + (MaxRiskPerContract > 0 ? MaxRiskPerContract.ToString() : "aus")
						+ ", RoundContractsUp=" + RoundContractsUp);
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

			// ---------- Break-Even-Management (Regel 7b) ----------
			// Muss VOR allen Signalfiltern stehen: Volumen-, Freitags- und sonstige
			// Einstiegsfilter duerfen die Verwaltung einer offenen Position nicht
			// blockieren. Bei offener Position ist danach Schluss — es gibt ohnehin
			// nur eine Position gleichzeitig (der fruehere Flat-Check weiter unten
			// ist hierher gewandert).
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				ManageBreakEven();
				return;
			}

			// ---------- Wochentagsfilter ----------
			// Steht bewusst NACH der Glattstellung und dem BE-Management: sperrt nur
			// neue Einstiege, eine offene Position wird an jedem Tag korrekt verwaltet
			// und beendet. Wochenend-Sessions sind grundsaetzlich gesperrt.
			bool dayAllowed;
			switch (Time[0].DayOfWeek)
			{
				case DayOfWeek.Monday:    dayAllowed = TradeMonday;    break;
				case DayOfWeek.Tuesday:   dayAllowed = TradeTuesday;   break;
				case DayOfWeek.Wednesday: dayAllowed = TradeWednesday; break;
				case DayOfWeek.Thursday:  dayAllowed = TradeThursday;  break;
				case DayOfWeek.Friday:    dayAllowed = TradeFriday;    break;
				default:                  dayAllowed = false;          break;
			}
			if (!dayAllowed)
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

			if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Tageslimit erreicht (" + tradesToday + ")");
				return;
			}

			// ---------- EMA-Durchbruch-Filter ----------
			// Verworfen wird, wenn die Signalkerze den EMA DURCHQUERT: Der Close liegt auf
			// der Handelsseite, der Open lag aber noch jenseits der Linie.
			// Referenz ist fuer Open und Close derselbe EMA-Wert dieser Kerze (ema[0]) —
			// also genau das, was man im Chart sieht: Die Linie laeuft durch den Koerper.
			//
			// Bei solchen Kerzen ist die Richtungsaussage des EMA am schwaechsten (der Kurs
			// steht praktisch auf der Linie), und der Einstieg zum Close laeuft der
			// Bewegung hinterher, die innerhalb der Kerze schon stattgefunden hat.
			if (UseEmaCrossFilter)
			{
				bool crossesEma = goLong ? Open[0] < ema[0] : Open[0] > ema[0];
				if (crossesEma)
				{
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Kerze quert den EMA (Open "
							+ Open[0] + " / EMA " + Math.Round(ema[0], 1) + " / Close " + Close[0] + ")");
					return;
				}
			}

			// ---------- Kerzenform-Filter ----------
			// Verworfen wird, wenn der Ablehnungsdocht laenger ist als MaxWickToBodyRatio
			// mal der Kerzenkoerper. "Ablehnungsdocht" heisst der Docht GEGEN die
			// Handelsrichtung: bei Long der obere (High - Close), bei Short der untere
			// (Close - Low). Ein langer Docht dort bedeutet, dass die Gegenseite den Kurs
			// innerhalb der Signalkerze schon deutlich zurueckgedrueckt hat.
			//
			// Weil der Koerper hier zugleich die strukturelle Stopdistanz ist, sagt die
			// Regel auch: Der bereits gelaufene Rueckschlag darf hoechstens das
			// Ratio-fache von 1R sein.
			if (UseCandleShapeFilter)
			{
				double body = Math.Abs(Close[0] - Open[0]);
				double wick = goLong ? High[0] - Close[0] : Close[0] - Low[0];

				if (wick > MaxWickToBodyRatio * body)
				{
					if (EnableDebugLog)
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Kerzenform: Docht "
							+ Math.Round(wick, 1) + " > " + MaxWickToBodyRatio + " x Body " + Math.Round(body, 1)
							+ " (Verhaeltnis " + (body > 0 ? Math.Round(wick / body, 2).ToString() : "unendlich") + ")");
					return;
				}
			}

			double pointValue = Instrument.MasterInstrument.PointValue;
			if (pointValue <= 0)
				return;                                    // bereits bei DataLoaded als Fehler geloggt

			// ---------- Strukturelle Stopdistanz aus dem Candle-Open ----------
			double structuralStop = Open[0];               // Open der Signalkerze
			double structDist     = goLong ? Close[0] - structuralStop : structuralStop - Close[0];

			// Stop auf der falschen Seite: rote Kerze bei Long bzw. gruene bei Short
			if (structDist <= 0)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Stop auf falscher Seite ("
						+ (goLong ? "rote" : "gruene") + " Kerze, Open=" + Open[0] + ", Close=" + Close[0] + ")");
				return;
			}

			// ---------- Stop-Deckel je Kontrakt (Regel 4b, aus der MAE-Auswertung) ----------
			// Gewinner-MAE lag im Backtest zu 90 % unter 26,50 je Kontrakt (Median 6,50);
			// der strukturelle Stop kostet im Schnitt 23,50. Die Stopdistanz wird deshalb
			// auf MaxRiskPerContract Geld je Kontrakt gedeckelt. Das Ziel rechnet weiter
			// aus der STRUKTURELLEN Distanz — der Deckel verkleinert nur das Risiko,
			// nicht den Gewinn.
			double useDist    = structDist;
			bool   isTightened = false;

			if (MaxRiskPerContract > 0 && structDist * pointValue > MaxRiskPerContract)
			{
				useDist     = MaxRiskPerContract / pointValue;
				isTightened = true;
			}

			// Mindest-Stopdistanz auf die FINALE Distanz pruefen — sie bestimmt die
			// Kontraktzahl und damit den Kostenblock.
			if (useDist < Math.Max(1, MinStopTicks) * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Stopdistanz " + useDist + " unter Mindestwert");
				return;
			}

			// ---------- Positionsgroesse ----------
			// Gerechnet mit der gedeckelten Distanz: engerer Stop -> mehr Kontrakte
			// bei gleichem Geldrisiko RiskAmount.
			int  qty      = 0;
			bool isCapped = false;

			if (RoundContractsUp)
			{
				// AUFRUNDEN + 1R-FIT (Regel 5): Kontraktzahl aufrunden, dann den Stop
				// exakt auf das 1R-Budget nachziehen. Ein Stopout kostet damit immer
				// genau RiskAmount (bis auf Tick-Rundung), die Kontraktzahl ist maximal.
				// Das Epsilon verhindert, dass Gleitkomma-Rauschen aus einer glatten
				// Division (z. B. exakt 8,0) faelschlich 9 Kontrakte macht.
				qty = (int)Math.Ceiling(RiskAmount / (useDist * pointValue) - 1e-9);
				qty = Math.Max(1, Math.Min(qty, MaxContracts));

				double fitDist = RiskAmount / (qty * pointValue);
				if (fitDist < useDist)
				{
					useDist     = fitDist;
					isTightened = true;                   // Stop ist jetzt ein Geldabstand zum Fill
				}
				// fitDist >= useDist tritt nur auf, wenn MaxContracts die Kontraktzahl
				// begrenzt hat — dann bleibt der bisherige Stop und das Risiko liegt
				// unter RiskAmount (bewusst: MaxContracts hat Vorrang).

				// Der alte Kappungsfall ist hier automatisch abgedeckt: qty = 1,
				// fitDist = RiskAmount / PointValue.
			}
			else
			{
				// LEGACY (Regel 5b): abrunden, Risiko nie ueber RiskAmount.
				qty = (int)Math.Floor(RiskAmount / (useDist * pointValue));

				if (qty < 1)
				{
					// Kappungsfall (Regel 6): Selbst 1 Kontrakt riskiert mehr als
					// RiskAmount -> Stop auf das Geldlimit ziehen. Bei aktivem Deckel
					// <= RiskAmount praktisch unerreichbar, bleibt als Sicherheitsnetz.
					qty      = 1;
					useDist  = RiskAmount / pointValue;
					isCapped = true;
				}

				qty = Math.Min(qty, MaxContracts);
				if (qty < 1)
					return;
			}

			// Nach dem 1R-Fit die Mindest-Stopdistanz erneut pruefen — der Fit kann
			// die Distanz unter die Schwelle gezogen haben.
			if (useDist < Math.Max(1, MinStopTicks) * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: Signal verworfen — Stopdistanz " + Math.Round(useDist, 2) + " nach 1R-Fit unter Mindestwert");
				return;
			}

			// Zieldistanz: strukturell (Regel 7) — nur im Legacy-Kappungsfall aus der Geld-Distanz.
			double targetDist = RewardMultiple * (isCapped ? useDist : structDist);

			double stopLevel = goLong
				? Instrument.MasterInstrument.RoundToTickSize(Close[0] - useDist)
				: Instrument.MasterInstrument.RoundToTickSize(Close[0] + useDist);

			double targetLevel = goLong
				? Instrument.MasterInstrument.RoundToTickSize(Close[0] + targetDist)
				: Instrument.MasterInstrument.RoundToTickSize(Close[0] - targetDist);

			// Fuer die Nachrechnung nach dem Fill merken
			pendingStopIsMoney  = isTightened || isCapped;
			pendingStopDistance = useDist;
			pendingTargetDist   = targetDist;
			pendingStopLevel    = pendingStopIsMoney ? 0 : Instrument.MasterInstrument.RoundToTickSize(structuralStop);

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
					+ " | Stop=" + stopLevel + " (" + Math.Round(useDist, 1) + " Pkt"
						+ (isCapped ? ", GEKAPPT" : isTightened ? ", NACHGEZOGEN — Candle-Open waere " + Math.Round(structDist, 1) : ", Candle-Open") + ")"
					+ " | Risiko=" + Math.Round(useDist * pointValue * qty, 2)
					+ " | Ziel=" + targetLevel + " (" + Math.Round(targetDist, 1) + " Pkt)");
		}

		// Break-Even-Move (Regel 7b): Stop auf Einstieg (+ Offset) ziehen, sobald die
		// abgeschlossene Kerze BreakEvenTriggerMultiple x finale Stopdistanz im Gewinn
		// stand. Wird pro Position genau einmal angewendet und zieht den Stop nur in
		// Gewinnrichtung. Schliesst die Trigger-Kerze bereits wieder jenseits des
		// BE-Levels, wird konservativ zum Close per Market geschlossen ("BEExit") —
		// ein Stop-Auftrag auf der falschen Marktseite waere ungueltig.
		private void ManageBreakEven()
		{
			if (!UseBreakEven || beApplied)
				return;
			if (activeStopDistance <= 0)
				return;                                    // noch kein Fill verarbeitet

			bool   isLong  = Position.MarketPosition == MarketPosition.Long;
			double entry   = Position.AveragePrice;
			double trigger = isLong
				? entry + BreakEvenTriggerMultiple * activeStopDistance
				: entry - BreakEvenTriggerMultiple * activeStopDistance;

			bool triggered = isLong ? High[0] >= trigger : Low[0] <= trigger;
			if (!triggered)
				return;

			double beStop = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? entry + BreakEvenOffsetTicks * TickSize
				       : entry - BreakEvenOffsetTicks * TickSize);

			beApplied = true;

			// Kurs schon wieder jenseits des BE-Levels: Market-Exit statt ungueltigem Stop.
			bool priceBeyondBe = isLong ? Close[0] <= beStop : Close[0] >= beStop;
			if (priceBeyondBe)
			{
				if (isLong) ExitLong("BEExit", SignalLong);
				else        ExitShort("BEExit", SignalShort);

				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: BE-Exit @ " + Close[0]
						+ " (Trigger " + Math.Round(trigger, 2) + " erreicht, Close schon jenseits BE " + beStop + ")");
				return;
			}

			if (isLong) SetStopLoss(SignalLong,  CalculationMode.Price, beStop, false);
			else        SetStopLoss(SignalShort, CalculationMode.Price, beStop, false);

			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " VSE: BE-Move — Stop auf " + beStop
					+ " (Einstieg " + Math.Round(entry, 2) + ", Trigger " + Math.Round(trigger, 2)
					+ " = " + BreakEvenTriggerMultiple + " x " + Math.Round(activeStopDistance, 2) + " Pkt)");
		}

		// Stop und Ziel auf den tatsaechlichen Fill nachrechnen.
		// Normalfall: Stop bleibt auf dem Candle-Open (struktureller Level).
		// Deckel-/Kappungsfall: Stop behaelt den Geldabstand zum Fill, damit das Risiko
		// exakt stimmt. Das Ziel behaelt immer seine Distanz zum Fill — im Normal- und
		// Deckelfall die strukturelle, im Kappungsfall die Geld-Distanz.
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
			if (pendingStopIsMoney)
				stop = isLong ? price - pendingStopDistance : price + pendingStopDistance;
			else
				stop = pendingStopLevel;

			stop = Instrument.MasterInstrument.RoundToTickSize(stop);

			double risk = isLong ? price - stop : stop - price;
			if (risk < TickSize)
				return;                                    // Sicherheitsnetz, Level bleibt wie gesetzt

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price + pendingTargetDist : price - pendingTargetDist);

			// Basis fuer den Break-Even-Move (Regel 7b): finale Stopdistanz dieser
			// Position; ein neuer Fill setzt den BE-Status zurueck.
			activeStopDistance = risk;
			beApplied          = false;

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

		// Beim Glattstellen den Break-Even-Status zuruecksetzen, damit die naechste
		// Position sauber startet.
		protected override void OnPositionUpdate(Position position, double averagePrice,
			int quantity, MarketPosition marketPosition)
		{
			if (marketPosition == MarketPosition.Flat)
			{
				beApplied          = false;
				activeStopDistance = 0;
			}
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
		[Display(Name = "Montag handeln", Description = "True (Standard): montags neue Positionen zulassen. Die Wochentagsauswertung 2020–2026 zeigte Montag als einzigen klar profitablen Tag — vor Live-Einsatz auf Out-of-Sample validieren. Alle Wochentagsschalter sperren nur neue Einstiege; offene Positionen werden immer regulaer verwaltet.", Order = 6, GroupName = "01 Zeiten")]
		public bool TradeMonday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dienstag handeln", Description = "False (Standard): dienstags keine neuen Positionen.", Order = 7, GroupName = "01 Zeiten")]
		public bool TradeTuesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mittwoch handeln", Description = "False (Standard): mittwochs keine neuen Positionen. Mittwoch war in der Auswertung der klar schlechteste Tag.", Order = 8, GroupName = "01 Zeiten")]
		public bool TradeWednesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Donnerstag handeln", Description = "False (Standard): donnerstags keine neuen Positionen.", Order = 9, GroupName = "01 Zeiten")]
		public bool TradeThursday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Freitags handeln", Description = "False (Standard): freitags keine neuen Positionen.", Order = 10, GroupName = "01 Zeiten")]
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
		[Display(Name = "Kerzenform-Filter aktiv", Description = "True (Standard): Signalkerzen mit zu langem Ablehnungsdocht werden verworfen.", Order = 15, GroupName = "02 Signal")]
		public bool UseCandleShapeFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 20)]
		[Display(Name = "Max. Docht/Body-Verhaeltnis", Description = "Wie lang der Ablehnungsdocht hoechstens sein darf, als Vielfaches des Kerzenkoerpers. Bei Long zaehlt der obere Docht (High - Close), bei Short der untere (Close - Low). Standard 2,0: Docht laenger als das Doppelte des Koerpers -> kein Trade. Kleinere Werte filtern strenger.", Order = 16, GroupName = "02 Signal")]
		public double MaxWickToBodyRatio { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "EMA-Durchbruch ausschliessen", Description = "True (Standard): Kerzen, die den EMA durchqueren (Open jenseits, Close diesseits), erzeugen kein Signal. Der Open muss bereits auf derselben Seite liegen wie der Close.", Order = 17, GroupName = "02 Signal")]
		public bool UseEmaCrossFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade (1R)", Description = "Geldbetrag in INSTRUMENTENWAEHRUNG, den ein Stopout kostet. FDXS rechnet in EUR -> 100 = 100 EUR.", Order = 20, GroupName = "03 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel (Reward-Multiple)", Description = "Take-Profit = Einstieg +/- Multiple x STRUKTURELLE Stopdistanz (Candle-Open). Standard 1. Das ist der Wert zum Durchtesten verschiedener R-Ziele. Bei aktivem Stop-Deckel liegt das effektive CRV entsprechend hoeher.", Order = 21, GroupName = "03 Risiko")]
		public double RewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Max. Risiko je Kontrakt (0 = aus)", Description = "Deckelt die Stopdistanz auf diesen Geldbetrag je Kontrakt (Instrumentenwaehrung). Aus der MAE-Auswertung: Gewinner-MAE lag zu 90 % unter 26,50/Kontrakt (Median 6,50), der strukturelle Stop im Schnitt bei 23,50. Standard 12,50 = Simulationsoptimum des Backtests (curve-fitting-anfaellig; ~26,50 = konservative p90-Variante). Das TP-Ziel rechnet weiterhin aus der strukturellen Distanz. Engerer Stop bedeutet mehr Kontrakte bei gleichem 1R — Kommission beachten. 0 = Deckel aus, Verhalten wie bisher.", Order = 22, GroupName = "03 Risiko")]
		public double MaxRiskPerContract { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Kontrakte aufrunden (exaktes 1R)", Description = "True (Standard): Kontraktzahl wird AUFGERUNDET und der Stop exakt auf das 1R-Budget nachgezogen — ein Stopout kostet immer genau RiskAmount, die Kontraktzahl ist maximal. Der nachgezogene Stop ist dann ein Geldabstand zum Fill statt des Candle-Opens; das TP-Ziel bleibt strukturell. False: Legacy-Verhalten mit Abrunden (Risiko nie ueber, oft unter 1R, Stop bleibt auf dem Candle-Open).", Order = 23, GroupName = "03 Risiko")]
		public bool RoundContractsUp { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Description = "Obergrenze der berechneten Positionsgroesse. Greift bei sehr engen Stops und hat Vorrang vor dem 1R-Fit (dann liegt das Risiko unter RiskAmount).", Order = 24, GroupName = "03 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mindest-Stopdistanz (Ticks)", Description = "Signale mit kleinerer FINALER Stopdistanz (nach Deckel und 1R-Fit) verwerfen. Wichtig, weil die Positionsgroesse invers zur Stopdistanz waechst: Ein 5-Punkte-Stop bedeutet 20 Kontrakte und damit den 20-fachen Kostenblock bei gleichem 1R. Standard 10. 0 = Filter aus.", Order = 25, GroupName = "03 Risiko")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Break-Even-Move aktiv", Description = "False (Standard): Der BE-Move war in den Tick-Backtests (Stand 08/2026) in keiner Konfiguration besser als ohne. Bei True wird der Stop auf den Einstieg (+ Offset) gezogen, sobald der Kurs den Trigger erreicht hat. Achtung: neutralisiert Verlierer, stoppt aber auch spaetere Gewinner auf null aus — Nettoeffekt nach Filteraenderungen erneut per Backtest/Optimizer pruefen.", Order = 1, GroupName = "04 Trade-Management")]
		public bool UseBreakEven { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 20)]
		[Display(Name = "BE-Trigger (x Stopdistanz)", Description = "Ab welchem Vielfachen der FINALEN Stopdistanz (nach Deckel und 1R-Fit) der Stop auf Einstieg gezogen wird. Standard 1,0 = der Kurs stand 1x Stopdistanz (also 1R) im Gewinn. Kandidat fuer den Optimizer: 0,5–3,0 in 0,25er-Schritten.", Order = 2, GroupName = "04 Trade-Management")]
		public double BreakEvenTriggerMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(-50, 200)]
		[Display(Name = "BE-Offset (Ticks)", Description = "Versatz des BE-Stops gegenueber dem Einstieg, in Gewinnrichtung. 0 (Standard) = exakt Einstieg. Positive Werte sichern einen Kostenpuffer (MNQ: ~3 Ticks decken die Roundturn-Kommission), negative geben dem Trade Luft unter dem Einstieg.", Order = 3, GroupName = "04 Trade-Management")]
		public int BreakEvenOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 200)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt. Es ist ohnehin immer nur eine Position gleichzeitig offen.", Order = 30, GroupName = "05 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Schreibt jedes Signal (inkl. Volumenverhaeltnis, EMA, Stopdistanz, Deckel/Kappung, Risiko), jeden Fill, jeden BE-Move und jeden Zeit-Exit ins NinjaScript Output-Fenster.", Order = 40, GroupName = "06 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}