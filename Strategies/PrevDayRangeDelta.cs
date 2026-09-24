#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
// PrevDayRangeDelta  —  DELTA-Kerze jenseits des Vortageshochs / Vortagestiefs
//                          NinjaTrader 8.1, bewusst einfach gehalten
//
// Regelwerk (vollstaendig):
//  1. Handelsfenster: Einstiege nur zwischen 15:30 und 17:00 (lokale NT-Zeitzone).
//  2. Referenzlevel aus der letzten ABGESCHLOSSENEN Tageskerze: PDH = Highs[1][0],
//     PDL = Lows[1][0]. NinjaTrader liefert von Zusatzserien nur fertige Bars, der
//     heutige Tag fliesst also nicht ein — kein Blick in die Zukunft.
//  3. Long nur, wenn die Kerze OBERHALB des PDH schliesst; Short nur, wenn sie
//     UNTERHALB des PDL schliesst. Das Level ist die Erlaubnis.
//  4. Ausloeser ist eine DELTA-KERZE: Das Netto-Aggressorvolumen der Signalkerze
//     (Kaeufe minus Verkaeufe, per Tick-Rule aus einer 1-Tick-Zusatzserie: Uptick =
//     Kaeufer, Downtick = Verkaeufer, unveraendert = Richtung des Vorticks) muss
//     1. in Handelsrichtung zeigen (Long: Delta > 0, Short: Delta < 0),
//     2. betragsmaessig mindestens DeltaMultiple (Standard 2,0) x den Durchschnitt
//        der absoluten Deltas der DeltaLookback VORHERGEHENDEN Kerzen erreichen und
//     3. optional (MinDeltaRatio > 0) mindestens diesen Anteil des Kerzenvolumens
//        ausmachen (|Delta| / Volumen, 0,3 = 30 % Netto-Aggression).
//     Kerzenfarbe ist bewusst KEINE Bedingung mehr — das Delta ersetzt sie.
//     Hinweis: Tick-Rule ist eine Naeherung ohne Bid/Ask-Stempel; sie funktioniert
//     historisch ohne Order Flow+ und ohne Tick Replay, braucht aber 1-Tick-Daten
//     (Backtests entsprechend langsamer).
//  5. Stop-Loss auf dem OPEN der Signalkerze. Bei einer gruenen Kerze liegt das Open
//     unter dem Close, bei einer roten darueber — der Stop sitzt also automatisch auf
//     der richtigen Seite. MinStopTicks / MaxStopTicks verwerfen Signale, deren
//     Abstand zu eng (Rauschen, absurde Positionsgroesse) oder zu weit ist.
//  6. Ziel = R-Multiple x Stopdistanz, fuer Long und Short getrennt einstellbar
//     (Standard je 3R), nach dem Fill auf den echten Einstiegskurs nachgerechnet.
//     Optional eigenes R-Ziel fuer Mittwoch/Donnerstag (hat Vorrang).
//  7. Positionsgroesse aus festem Geldrisiko. Max. MaxTradesPerDay Trades pro Tag.
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// Hinweis: PDH/PDL stammen aus der Tageskerze gemaess dem eingestellten Trading-Hours-
// Template. Bei Futures umfasst die Tageskerze die volle Session, nicht nur die Kassazeit.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class PrevDayRangeDelta : Strategy
	{
		private const string SignalLong  = "PDD_Long";
		private const string SignalShort = "PDD_Short";

		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;

		// Delta-Infrastruktur (Tick-Rule ueber die 1-Tick-Zusatzserie)
		private double tickLast;         // letzter Tickpreis
		private int    tickDir;          // letzte Klassifikation (+1 Kauf / -1 Verkauf)
		private double curDelta;         // laufendes Delta der aktuellen Primaerkerze
		private double curVol;           // laufendes Volumen der aktuellen Primaerkerze
		private double barDelta;         // Delta der soeben geschlossenen Signalkerze
		private double barVol;           // Volumen der soeben geschlossenen Signalkerze
		private readonly List<double> deltaHistory = new List<double>();  // |Delta| je Kerze
		private double activeStopLevel;
		private double activeReward;      // R-Ziel des laufenden Trades (kann am Mi/Do abweichen)
		private double activeEntryPrice;  // echter Fill-Kurs (fuer Break-Even)
		private double activeRisk;        // Risiko je Einheit ab Fill (fuer Break-Even)
		private bool   beMoved;           // Break-Even wurde fuer diesen Trade schon gezogen

		// ---------- Signal-Bridge ----------
		// Ein HttpClient fuer alle Instanzen; niemals im Strategie-Thread blockieren.
		private static readonly HttpClient signalHttp = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
		private string   currentTradeId;      // eindeutige Kennung des laufenden Trades
		private bool     entrySignalSent;     // Entry nur einmal senden (Teilfills)
		private bool     senderBlocked;       // z. B. Nicht-Sim-Konto -> Sender aus
		private DateTime lastHeartbeatUtc = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Delta-Kerze (Netto-Aggressorvolumen per Tick-Rule in Handelsrichtung) oberhalb des Vortageshochs bzw. unterhalb des Vortagestiefs, 15:30-17:00. Stop auf dem Open der Signalkerze, Ziel 3R.";
				Name                            = "PrevDayRangeDelta";
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
				TradeSaturday        = true;  // fuer Krypto; ohne Wochenend-Bars wirkungslos
				TradeSunday          = true;
				// Einstieg
				DeltaMultiple        = 2.0;   // 200 % des Durchschnitts-|Deltas| ...
				DeltaLookback        = 20;    // ... der 20 VORHERGEHENDEN Kerzen
				MinDeltaRatio        = 0.0;   // 0 = aus; sonst |Delta|/Volumen-Mindestanteil
				AllowLong            = true;
				AllowShort           = true;
				// Risiko
				MinStopTicks         = 8;
				MaxStopTicks         = 120;
				RewardMultipleLong   = 3;
				RewardMultipleShort  = 3;
				UseMidweekReward     = false; // Standard AUS — Analyse spricht dagegen, s. README
				MidweekRewardMultiple = 2;
				UseBreakEvenLong     = true;  // zum Testen: Trigger 3R bei Ziel 4R
				UseBreakEvenShort    = false;
				BreakEvenTriggerR    = 3.0;
				BreakEvenOffsetR     = 0.0;
				UseFixedRisk         = true;
				RiskAmount           = 100;
				MaxContracts         = 50;
				Contracts            = 1;
				MaxTradesPerDay      = 1;
				EnableDebugLog       = false;
				// Signal-Bridge
				EnableSignalSender   = false;  // Standard AUS — nur fuer den Live-Signalbetrieb
				SignalUrl            = "";
				SignalSecret         = "";
				HeartbeatMinutes     = 5;
			}
			else if (State == State.Configure)
			{
				// Tagesserie fuer PDH/PDL — Pflicht, nicht optional
				AddDataSeries(BarsPeriodType.Day, 1);      // Index 1: PDH/PDL
				AddDataSeries(BarsPeriodType.Tick, 1);     // Index 2: Delta per Tick-Rule

				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, DeltaLookback + 1);
			}
			else if (State == State.DataLoaded)
			{
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
					Log(Name + ": Bitte eine Minuten-Datenserie verwenden (aktuell: " + BarsPeriod + ").", LogLevel.Warning);

				if (UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Positionsgroesse aus Geldrisiko nicht berechenbar, es wird auf "
						+ Contracts + " Kontrakt(e) zurueckgefallen.", LogLevel.Warning);

				if (UseMidweekReward && !TradeWednesday && !TradeThursday)
					Log(Name + ": 'R-Ziel Mi/Do separat' ist AN, aber Mittwoch und Donnerstag sind beide"
						+ " abgeschaltet — die Einstellung hat so keine Wirkung.", LogLevel.Warning);

				// Trigger >= Ziel: das Ziel wird immer zuerst erreicht, der Break-Even
				// zieht dann nie — derselbe Totlauf wie damals beim Trailing-Stop.
				if (UseBreakEvenLong && AllowLong && BreakEvenTriggerR >= RewardMultipleLong)
					Log(Name + ": Break-Even Long ist AN, aber Trigger (" + BreakEvenTriggerR
						+ "R) >= R-Ziel Long (" + RewardMultipleLong + "R) — das Ziel fuellt zuerst,"
						+ " der Break-Even wirkt nie. R-Ziel Long erhoehen (z. B. 4) oder Trigger senken.", LogLevel.Warning);
				if (UseBreakEvenShort && AllowShort && BreakEvenTriggerR >= RewardMultipleShort)
					Log(Name + ": Break-Even Short ist AN, aber Trigger (" + BreakEvenTriggerR
						+ "R) >= R-Ziel Short (" + RewardMultipleShort + "R) — das Ziel fuellt zuerst,"
						+ " der Break-Even wirkt nie.", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — " + Instrument.FullName
						+ " | Fenster " + StartHour.ToString("00") + ":" + StartMinute.ToString("00")
						+ "-" + EndHour.ToString("00") + ":" + EndMinute.ToString("00")
						+ " | Delta " + (DeltaMultiple * 100) + " % ueber " + DeltaLookback + " Kerzen"
						+ (MinDeltaRatio > 0 ? " | MinRatio " + MinDeltaRatio : "")
						+ " | Ziel Long " + RewardMultipleLong + "R / Short " + RewardMultipleShort + "R");
			}
			else if (State == State.Realtime)
			{
				// Schutzgurt: Der Sender laeuft NUR auf einem Sim-Konto. Auf einem echten
				// Konto wird er hart deaktiviert — echte Orders macht nur die Bridge bei Propr.
				if (EnableSignalSender && Account != null
					&& Account.Name.IndexOf("Sim", StringComparison.OrdinalIgnoreCase) < 0
					&& Account.Name.IndexOf("Playback", StringComparison.OrdinalIgnoreCase) < 0)
				{
					senderBlocked = true;
					Log(Name + ": Signal-Sender ist AN, aber die Strategie laeuft auf dem Konto '"
						+ Account.Name + "' (kein Sim-Konto). Sender wurde DEAKTIVIERT — bitte auf Sim101 laufen lassen.",
						LogLevel.Error);
				}
				if (EnableSignalSender && string.IsNullOrWhiteSpace(SignalUrl))
					Log(Name + ": Signal-Sender ist AN, aber keine Signal-URL gesetzt — es wird nichts gesendet.", LogLevel.Warning);
				if (SenderActive())
					Log(Name + ": Signal-Sender aktiv — " + SignalUrl, LogLevel.Information);
			}
		}

		// ---------- Signal-Bridge ----------
		// Sicherung: Der Sender arbeitet nur im Realtime-Betrieb, nur mit URL/Secret und
		// NIE auf einem echten Konto — die Ausfuehrung gehoert auf Sim101, echte Orders
		// macht ausschliesslich die Bridge bei Propr.
		private bool SenderActive()
		{
			return EnableSignalSender
				&& !senderBlocked
				&& State == State.Realtime
				&& !string.IsNullOrWhiteSpace(SignalUrl);
		}

		private static string JsonStr(string s)
		{
			if (s == null) return "null";
			var sb = new StringBuilder("\"");
			foreach (char c in s)
			{
				if (c == '"' || c == '\\') sb.Append('\\').Append(c);
				else if (c < ' ') sb.Append(' ');
				else sb.Append(c);
			}
			return sb.Append('"').ToString();
		}

		private static string JsonNum(double v)
		{
			return v.ToString("0.########", CultureInfo.InvariantCulture);
		}

		// Fire-and-forget mit 3 Wiederholungen — blockiert nie den Strategie-Thread.
		private void SendSignal(string json)
		{
			string url = SignalUrl, secret = SignalSecret;
			Task.Run(async () =>
			{
				for (int attempt = 1; attempt <= 3; attempt++)
				{
					try
					{
						var req = new HttpRequestMessage(HttpMethod.Post, url);
						req.Headers.TryAddWithoutValidation("X-Signal-Secret", secret);
						req.Content = new StringContent(json, Encoding.UTF8, "application/json");
						var resp = await signalHttp.SendAsync(req).ConfigureAwait(false);
						if (resp.IsSuccessStatusCode)
							return;
						NinjaTrader.Code.Output.Process(Name + " Signal: HTTP " + (int)resp.StatusCode
							+ " (Versuch " + attempt + "/3)", PrintTo.OutputTab1);
					}
					catch (Exception ex)
					{
						NinjaTrader.Code.Output.Process(Name + " Signal: " + ex.Message
							+ " (Versuch " + attempt + "/3)", PrintTo.OutputTab1);
					}
					await Task.Delay(2000 * attempt).ConfigureAwait(false);
				}
				NinjaTrader.Code.Output.Process(Name + " Signal: endgueltig fehlgeschlagen — " + json, PrintTo.OutputTab1);
			});
		}

		private void SendEntrySignal(bool isLong, double fillPrice, int quantity)
		{
			double stopPct = fillPrice > 0 ? activeRisk / fillPrice * 100.0 : 0;
			string json = "{"
				+ "\"event\":\"entry\""
				+ ",\"nt_signal_id\":" + JsonStr(currentTradeId)
				+ ",\"direction\":\"" + (isLong ? "long" : "short") + "\""
				+ ",\"instrument\":" + JsonStr(Instrument.FullName)
				+ ",\"entry_price\":" + JsonNum(fillPrice)
				+ ",\"stop_price\":" + JsonNum(activeStopLevel)
				+ ",\"stop_pct\":" + JsonNum(stopPct)
				+ ",\"reward_multiple\":" + JsonNum(activeReward)
				+ ",\"quantity\":" + quantity
				+ ",\"time\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
				+ "}";
			SendSignal(json);
		}

		private void SendExitSignal(string reason, double fillPrice)
		{
			string json = "{"
				+ "\"event\":\"exit\""
				+ ",\"nt_signal_id\":" + JsonStr(currentTradeId)
				+ ",\"exit_reason\":\"" + reason + "\""
				+ ",\"exit_price\":" + JsonNum(fillPrice)
				+ ",\"time\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
				+ "}";
			SendSignal(json);
		}

		private void MaybeSendHeartbeat()
		{
			if (!SenderActive())
				return;
			if ((DateTime.UtcNow - lastHeartbeatUtc).TotalMinutes < Math.Max(1, HeartbeatMinutes))
				return;
			lastHeartbeatUtc = DateTime.UtcNow;
			SendSignal("{\"event\":\"heartbeat\",\"instrument\":" + JsonStr(Instrument.FullName)
				+ ",\"time\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\"}");
		}

		// Delta-Kerze: Netto-Aggressorvolumen in Handelsrichtung, deutlich ueber dem
		// Durchschnitt der VORHERGEHENDEN Kerzen (die Signalkerze zaehlt nicht in ihre
		// eigene Messlatte). barDelta/barVol gehoeren zur soeben geschlossenen Kerze.
		private bool IsDeltaCandle(bool isLong)
		{
			if (deltaHistory.Count < DeltaLookback + 1)
				return false;                              // History inkl. aktueller Kerze

			// Durchschnitt der |Deltas| der vorhergehenden Kerzen (aktuelle ausnehmen)
			double sum = 0;
			for (int i = deltaHistory.Count - 1 - DeltaLookback; i < deltaHistory.Count - 1; i++)
				sum += deltaHistory[i];
			double avg = sum / DeltaLookback;
			if (avg <= 0)
				return false;

			bool directionOk = isLong ? barDelta > 0 : barDelta < 0;
			bool sizeOk      = Math.Abs(barDelta) >= DeltaMultiple * avg;
			bool ratioOk     = MinDeltaRatio <= 0
				|| (barVol > 0 && Math.Abs(barDelta) / barVol >= MinDeltaRatio);
			return directionOk && sizeOk && ratioOk;
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
			// ---------- 1-Tick-Serie: Delta per Tick-Rule akkumulieren ----------
			if (BarsInProgress == 2)
			{
				double p = Closes[2][0];
				double v = Volumes[2][0];
				int dir = p > tickLast ? 1 : p < tickLast ? -1 : tickDir;
				if (tickLast > 0 && dir != 0)
				{
					curDelta += dir * v;
					tickDir   = dir;
				}
				curVol  += v;
				tickLast = p;
				return;
			}
			if (BarsInProgress != 0)
				return;

			// Delta der soeben geschlossenen Primaerkerze festschreiben und zuruecksetzen
			barDelta = curDelta;
			barVol   = curVol;
			curDelta = 0;
			curVol   = 0;
			deltaHistory.Add(Math.Abs(barDelta));
			if (deltaHistory.Count > 1000)
				deltaHistory.RemoveRange(0, deltaHistory.Count - 500);

			if (CurrentBars[0] < 1 || CurrentBars.Length < 3 || CurrentBars[1] < 1)
				return;                                    // noch keine abgeschlossene Tageskerze

			if (Time[0].Date != currentDay)
			{
				currentDay  = Time[0].Date;
				tradesToday = 0;
			}

			MaybeSendHeartbeat();

			// Break-Even VOR der Fensterlogik verwalten, damit er auch greift, wenn die
			// Position (CloseAtWindowEnd = false) ueber das Fensterende hinaus laeuft.
			if (Position.MarketPosition != MarketPosition.Flat)
				ManageBreakEven();

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
						Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: Zeit-Ausstieg @ " + Close[0]);
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

			// ---------- Long: Delta-Kerze schliesst oberhalb des Vortageshochs ----------
			if (AllowLong && Close[0] > pdh && IsDeltaCandle(true))
			{
				TryEnter(true);
				return;
			}

			// ---------- Short: Delta-Kerze schliesst unterhalb des Vortagestiefs ----------
			if (AllowShort && Close[0] < pdl && IsDeltaCandle(false))
				TryEnter(false);
		}

		// R-Ziel fuer Tag und Richtung: Mittwoch/Donnerstag koennen ein eigenes
		// (typischerweise kleineres) Multiple bekommen — das hat Vorrang. Sonst gilt
		// das Long- bzw. Short-R.
		private double RewardFor(DayOfWeek d, bool isLong)
		{
			if (UseMidweekReward && (d == DayOfWeek.Wednesday || d == DayOfWeek.Thursday))
				return MidweekRewardMultiple;
			return isLong ? RewardMultipleLong : RewardMultipleShort;
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
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: verworfen — Stop zu eng ("
						+ Math.Round(dist / TickSize, 0) + " Ticks < " + MinStopTicks + ")");
				return;
			}
			if (dist > MaxStopTicks * TickSize)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: verworfen — Stop zu weit ("
						+ Math.Round(dist / TickSize, 0) + " Ticks > " + MaxStopTicks + ")");
				return;
			}

			int qty = CalcQuantity(dist);
			if (qty < 1)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: verworfen — Kontraktzahl 0 (Stop "
						+ Math.Round(dist / TickSize, 0) + " Ticks)");
				return;
			}

			double reward = RewardFor(Time[0].DayOfWeek, isLong);
			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? Close[0] + reward * dist : Close[0] - reward * dist);

			activeStopLevel  = stopLevel;
			activeReward     = reward;
			activeEntryPrice = Close[0];   // vorlaeufig — OnExecutionUpdate ersetzt durch echten Fill
			activeRisk       = dist;
			beMoved          = false;
			currentTradeId   = Guid.NewGuid().ToString("N");
			entrySignalSent  = false;

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
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: " + (isLong ? "LONG " : "SHORT ") + qty
					+ " @ " + Close[0]
					+ " | Stop (Kerzen-Open) " + stopLevel + " (" + Math.Round(dist / TickSize, 0) + " Ticks)"
					+ " | Ziel " + target + " | Trade " + tradesToday + " heute");
		}

		// Break-Even: Hat der Kurs den Trigger (in R ab dem echten Fill) erreicht, wird
		// der Stop einmalig auf Einstand + Offset gezogen. Getrennt schaltbar fuer Long
		// und Short. Laeuft OnBarClose: Der Trigger gilt als erreicht, wenn High/Low der
		// abgeschlossenen Kerze ihn beruehrt hat; ob der Kurs INNERHALB dieser Kerze erst
		// den Trigger und dann den alten Stop anlief (oder umgekehrt), ist auf Kerzenbasis
		// nicht feststellbar — der Backtest ist hier also eher optimistisch.
		private void ManageBreakEven()
		{
			if (beMoved || activeRisk <= 0)
				return;

			bool isLong = Position.MarketPosition == MarketPosition.Long;
			if (isLong && !UseBreakEvenLong)
				return;
			if (!isLong && !UseBreakEvenShort)
				return;

			if (isLong)
			{
				if (High[0] < activeEntryPrice + BreakEvenTriggerR * activeRisk)
					return;
				double newStop = Instrument.MasterInstrument.RoundToTickSize(
					activeEntryPrice + BreakEvenOffsetR * activeRisk);
				newStop = Math.Min(newStop, Close[0] - TickSize);   // Stop muss unter dem Markt bleiben
				if (newStop <= activeStopLevel)
					return;                                          // nur verbessern, nie verschlechtern
				SetStopLoss(SignalLong, CalculationMode.Price, newStop, false);
				activeStopLevel = newStop;
			}
			else
			{
				if (Low[0] > activeEntryPrice - BreakEvenTriggerR * activeRisk)
					return;
				double newStop = Instrument.MasterInstrument.RoundToTickSize(
					activeEntryPrice - BreakEvenOffsetR * activeRisk);
				newStop = Math.Max(newStop, Close[0] + TickSize);   // Stop muss ueber dem Markt bleiben
				if (newStop >= activeStopLevel)
					return;
				SetStopLoss(SignalShort, CalculationMode.Price, newStop, false);
				activeStopLevel = newStop;
			}

			beMoved = true;
			if (EnableDebugLog)
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " PDD: Break-Even gezogen — Stop jetzt "
					+ activeStopLevel + " (Trigger " + BreakEvenTriggerR + "R, Offset " + BreakEvenOffsetR + "R)");
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

			string orderName = execution.Order.Name;

			// ---------- Exit-Fills: nur Signal senden, sonst nichts zu tun ----------
			if (orderName == "Stop loss" || orderName == "Profit target"
				|| orderName == "TimeExit" || orderName == "Exit on session close")
			{
				if (SenderActive() && currentTradeId != null)
				{
					string reason = orderName == "Stop loss" ? "stop"
						: orderName == "Profit target" ? "target" : "time";
					SendExitSignal(reason, price);
				}
				return;
			}

			bool isLong = orderName == SignalLong;
			if (!isLong && orderName != SignalShort)
				return;

			double risk = isLong ? price - activeStopLevel : activeStopLevel - price;
			if (risk < TickSize)
				return;

			activeEntryPrice = price;  // Break-Even rechnet ab dem echten Fill
			activeRisk       = risk;

			// ---------- Entry-Signal (einmal pro Trade, mit echtem Fill) ----------
			if (SenderActive() && !entrySignalSent)
			{
				entrySignalSent = true;
				SendEntrySignal(isLong, price, quantity);
			}

			double target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? price + activeReward * risk : price - activeReward * risk);

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
				Print(time.ToString("yyyy-MM-dd HH:mm") + " PDD: Fill @ " + price + " x" + quantity
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
		[Display(Name = "Samstag handeln", Description = "Nur fuer 24/7-Maerkte (Krypto) relevant.", Order = 15, GroupName = "02 Wochentage")]
		public bool TradeSaturday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sonntag handeln", Description = "Krypto und CME-Sonntagssession.", Order = 16, GroupName = "02 Wochentage")]
		public bool TradeSunday { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "Delta: Faktor", Description = "Betrag des Kerzen-Deltas muss mindestens dieses Vielfache des Durchschnitts-|Deltas| der vorhergehenden Kerzen erreichen. 2,0 = 200 Prozent (Standard).", Order = 20, GroupName = "03 Einstieg")]
		public double DeltaMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Delta: Durchschnitt ueber", Description = "Ueber wie viele VORHERGEHENDE Kerzen der |Delta|-Durchschnitt gebildet wird. Die Signalkerze selbst zaehlt nicht mit. Standard 20.", Order = 21, GroupName = "03 Einstieg")]
		public int DeltaLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1)]
		[Display(Name = "Delta: Mindestanteil am Volumen", Description = "0 = aus (Standard). Sonst muss |Delta|/Volumen der Signalkerze mindestens diesen Wert erreichen (0,3 = 30 % Netto-Aggression).", Order = 22, GroupName = "03 Einstieg")]
		public double MinDeltaRatio { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben",  Description = "Einstiege oberhalb des Vortageshochs zulassen.", Order = 23, GroupName = "03 Einstieg")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Description = "Einstiege unterhalb des Vortagestiefs zulassen.", Order = 24, GroupName = "03 Einstieg")]
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
		[Display(Name = "R-Ziel Long", Description = "Ziel fuer Longs = Einstieg + Multiple x Stopdistanz. Standard 3.", Order = 32, GroupName = "04 Risiko")]
		public double RewardMultipleLong { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel Short", Description = "Ziel fuer Shorts = Einstieg - Multiple x Stopdistanz. Standard 3.", Order = 33, GroupName = "04 Risiko")]
		public double RewardMultipleShort { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "R-Ziel Mi/Do separat", Description = "True: Mittwoch und Donnerstag nutzen das eigene R-Ziel unten statt des normalen. Standard AUS — die Datenanalyse spricht dagegen (s. README).", Order = 34, GroupName = "04 Risiko")]
		public bool UseMidweekReward { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "R-Ziel Mi/Do", Description = "Reward-Multiple nur fuer Mittwoch und Donnerstag. Wirkt nur, wenn 'R-Ziel Mi/Do separat' an ist UND die Tage ueberhaupt gehandelt werden.", Order = 35, GroupName = "04 Risiko")]
		public double MidweekRewardMultiple { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Break-Even Long aktiv", Description = "True (Standard): Bei Longs wird der Stop einmalig auf Einstand + Offset gezogen, sobald der Kurs den Trigger erreicht. Der Trigger muss UNTER dem R-Ziel liegen (z. B. Trigger 3, Ziel 4), sonst fuellt das Ziel zuerst.", Order = 41, GroupName = "04 Risiko")]
		public bool UseBreakEvenLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Break-Even Short aktiv", Description = "Wie Break-Even Long, nur fuer Shorts. Standard AUS.", Order = 42, GroupName = "04 Risiko")]
		public bool UseBreakEvenShort { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "Break-Even Trigger (R)", Description = "Ab wie viel R Gewinn (ab dem echten Fill) der Stop gezogen wird. Standard 3.", Order = 43, GroupName = "04 Risiko")]
		public double BreakEvenTriggerR { get; set; }

		[NinjaScriptProperty]
		[Range(-5, 10)]
		[Display(Name = "Break-Even Offset (R)", Description = "Wohin der Stop gezogen wird: 0 = exakt Einstand (Standard), 0,5 = ein halbes R im Gewinn gesichert, negativ = knapp unter/ueber Einstand.", Order = 44, GroupName = "04 Risiko")]
		public double BreakEvenOffsetR { get; set; }

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

		[NinjaScriptProperty]
		[Display(Name = "Signale senden", Description = "True: sendet Entry-/Exit-Fills und einen Heartbeat per HTTPS an die Signal-Bridge (Momentum/Propr). Nur im Realtime-Betrieb, NUR auf einem Sim-Konto. Standard AUS.", Order = 60, GroupName = "07 Signal-Bridge")]
		public bool EnableSignalSender { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Signal-URL", Description = "HTTPS-Endpunkt der Bridge (Supabase Edge Function).", Order = 61, GroupName = "07 Signal-Bridge")]
		public string SignalUrl { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Signal-Secret", Description = "Gemeinsames Geheimnis; wird als Header X-Signal-Secret mitgesendet. Kommt aus dem Momentum-Dashboard.", Order = 62, GroupName = "07 Signal-Bridge")]
		public string SignalSecret { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Heartbeat (Minuten)", Description = "Wie oft ein Lebenszeichen an die Bridge geht. Standard 5.", Order = 63, GroupName = "07 Signal-Bridge")]
		public int HeartbeatMinutes { get; set; }
		#endregion
	}
}
