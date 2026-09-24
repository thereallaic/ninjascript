#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
// MagnetZones  —  Ziel-Magneten plus Brodel-Trigger
//                 NinjaTrader 8.1; Trigger-Timeframe = Chartserie (empfohlen 5-min)
//
// GRUNDIDEE: Erst das Ziel, dann der Weg. Die Strategie fuehrt eine Liste von
// "Magneten" (Preiszonen, zu denen der Markt erfahrungsgemaess hingezogen wird)
// und steigt ein, wenn messbarer Druck ("Brodeln") in Richtung eines ausreichend
// weit entfernten Magneten entsteht. Das Ziel der Position IST der Magnet.
//
// ZIELE (Magneten):
//  1. UNRECOVERED VECTOR ZONES auf einem hoeheren Timeframe (Standard 15-min):
//     Kerze mit Volumen >= ZoneVolMultiple x Durchschnitt der ZoneVolLookback
//     VORHERGEHENDEN Kerzen UND Koerper >= ZoneBodyAtrMult x ATR der Zonenserie.
//     Ihr Koerper wird als Zone gespeichert. Die Zone verfaellt ("recovered"),
//     sobald der Preis den Koerper zu mindestens RecoverPct (0,5 = 50 %) gefuellt
//     hat, optional auch nach ZoneMaxAgeBars Zonen-Kerzen. Ziel ist immer die dem
//     Preis zugewandte Kante der Zone.
//  2. UNGETESTETE VORTAGESLEVEL (UsePdTargets): PDH/PDL aus der Tagesserie zaehlen
//     als Magnet, solange der heutige Handel sie noch nicht beruehrt hat.
//
// BRODELN (Trigger, auf der Chartserie):
//  - Delta je Kerze per Tick-Rule aus einer 1-Tick-Zusatzserie (Uptick = Kaeufer,
//    Downtick = Verkaeufer, unveraendert = Richtung des Vorticks).
//  - Signal, wenn die Summe der Deltas der letzten DeltaWindow Kerzen (inkl. der
//    aktuellen) mindestens DeltaSumMultiple x das Durchschnitts-|Delta| einer
//    einzelnen Kerze (ueber DeltaLookback vorherige Kerzen) betraegt — also netto
//    ueber mehrere Minuten einseitig in Zielrichtung gearbeitet wird.
//  - KOMPRESSION (UseCompression, Standard AN): Der Schub zaehlt nur, wenn er aus
//    einer Verdichtung kommt — ATR der Chartserie unter CompressionFactor x dem
//    langen ATR-Durchschnitt. Druck aus dem Kessel, nicht mitten im Trend.
//
// GEOMETRIE:
//  - Richtung = Vorzeichen der Delta-Summe. Ziel = naechster aktiver Magnet in
//    dieser Richtung (Zonenkante bzw. PDH/PDL), abzueglich TargetBufferTicks.
//  - Stop = Signal-Close -/+ StopAtrMultiple x ATR(Chartserie).
//  - EINSTIEG NUR, wenn Distanz zum Ziel / Stopdistanz >= MinRR UND (optional)
//    die Stopdistanz >= MinStopPct des Preises. Nahe Ziele werden ignoriert —
//    Mini-Scalps sind damit strukturell ausgeschlossen, nicht nur gefiltert.
//  - Kein Trailing in v1: Stop und Ziel bleiben stehen (das Ziel ist die These).
//
// Zeitzone: Tools > Options > General > Time zone muss auf Berlin stehen.
// Hinweis: 1-Tick-Daten noetig (Delta); Backtests entsprechend langsamer.
// =====================================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
	public class MagnetZones : Strategy
	{
		private const string SignalLong  = "MGZ_Long";
		private const string SignalShort = "MGZ_Short";

		private class Zone
		{
			public double Top;
			public double Bottom;
			public int    CreatedBar;      // Bar-Index der Zonenserie
			public double Height { get { return Top - Bottom; } }
		}

		private readonly List<Zone> zones = new List<Zone>();

		private DateTime currentDay = DateTime.MinValue;
		private int      tradesToday;
		private bool     pdhTested;
		private bool     pdlTested;

		private SMA    zoneVolAvg;       // Durchschnittsvolumen der Zonenserie
		private ATR    zoneAtr;          // ATR der Zonenserie (Koerper-Massstab)
		private ATR    trigAtr;          // ATR der Chartserie (Stop + Kompression)
		private SMA    trigAtrAvg;       // langer ATR-Durchschnitt (Kompression)

		// Delta-Infrastruktur (Tick-Rule, 1-Tick-Serie)
		private double tickLast;
		private int    tickDir;
		private double curDelta;
		private double barDelta;
		private readonly List<double> deltaHistory = new List<double>();  // Delta je Kerze (mit Vorzeichen)

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                     = "Ziel-Magneten (unrecovered Vector-Zonen auf hoeherem Timeframe, ungetestete Vortageslevel) plus Brodel-Trigger: Delta-Schub aus einer Kompression in Richtung des Magneten. Ziel der Position ist der Magnet; Einstieg nur ab MinRR.";
				Name                            = "MagnetZones";
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
				BarsRequiredToTrade             = 30;
				IsInstantiatedOnEachOptimizationIteration = true;

				// 01 Zeiten — Standard AUS (ganztaegig)
				UseTimeWindow        = false;
				StartHour            = 8;
				StartMinute          = 0;
				EndHour              = 22;
				EndMinute            = 0;
				// 02 Wochentage
				TradeMonday          = true;
				TradeTuesday         = true;
				TradeWednesday       = true;
				TradeThursday        = true;
				TradeFriday          = true;
				TradeSaturday        = true;
				TradeSunday          = true;
				// 03 Ziele (Magneten)
				ZoneTfMinutes        = 15;
				ZoneVolMultiple      = 2.0;
				ZoneVolLookback      = 20;
				ZoneBodyAtrMult      = 1.5;
				ZoneAtrPeriod        = 14;
				RecoverPct           = 0.5;
				ZoneMaxAgeBars       = 0;     // 0 = unbegrenzt
				UsePdTargets         = true;
				TargetBufferTicks    = 4;
				// 04 Brodeln (Trigger)
				DeltaWindow          = 5;
				DeltaSumMultiple     = 2.0;
				DeltaLookback        = 20;
				UseCompression       = true;
				CompressionFactor    = 0.85;
				CompressionAvgPeriod = 100;
				// 05 Risiko
				StopAtrMultiple      = 1.5;
				TrigAtrPeriod        = 14;
				MinRR                = 2.0;
				MinStopPct           = 0.0;   // 0 = aus; z. B. 0,3 fuer BTC-Perp-Realismus
				UseFixedRisk         = true;
				RiskAmount           = 100;
				MaxContracts         = 50;
				Contracts            = 1;
				AllowLong            = true;
				AllowShort           = true;
				// 06 Verhalten
				MaxTradesPerDay      = 0;     // 0 = unbegrenzt
				// 07 Diagnose
				EnableDebugLog       = false;
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Minute, ZoneTfMinutes);  // Index 1: Zonenserie
				AddDataSeries(BarsPeriodType.Day, 1);                 // Index 2: PDH/PDL
				AddDataSeries(BarsPeriodType.Tick, 1);                // Index 3: Delta

				BarsRequiredToTrade = Math.Max(BarsRequiredToTrade,
					Math.Max(DeltaLookback + DeltaWindow, Math.Max(TrigAtrPeriod, CompressionAvgPeriod)) + 1);
			}
			else if (State == State.DataLoaded)
			{
				zoneVolAvg = SMA(Volumes[1], ZoneVolLookback);
				zoneAtr    = ATR(BarsArray[1], ZoneAtrPeriod);
				trigAtr    = ATR(TrigAtrPeriod);
				trigAtrAvg = SMA(trigAtr, CompressionAvgPeriod);

				if (UseFixedRisk && Instrument.MasterInstrument.PointValue <= 0)
					Log(Name + ": PointValue ist " + Instrument.MasterInstrument.PointValue
						+ " (<= 0). Groesse aus Geldrisiko nicht berechenbar, Rueckfall auf "
						+ Contracts + " Kontrakt(e).", LogLevel.Warning);

				if (EnableDebugLog)
					Print(Name + ": Start — Zonen " + ZoneTfMinutes + "min (Vol x" + ZoneVolMultiple
						+ ", Koerper x" + ZoneBodyAtrMult + " ATR, Recover " + (RecoverPct * 100) + " %)"
						+ " | Brodeln: Delta-Summe x" + DeltaSumMultiple + " ueber " + DeltaWindow + " Kerzen"
						+ (UseCompression ? " + Kompression <" + CompressionFactor : "")
						+ " | MinRR " + MinRR);
			}
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

		// ---------- Zonenpflege (laeuft auf der Zonenserie) ----------
		private void UpdateZones()
		{
			double high  = Highs[1][0];
			double low   = Lows[1][0];
			double open  = Opens[1][0];
			double close = Closes[1][0];

			// Schritt 1: Recovery-Pruefung: Wie tief hat diese Kerze in bestehende Zonen gegriffen?
			for (int i = zones.Count - 1; i >= 0; i--)
			{
				Zone z = zones[i];
				if (z.CreatedBar == CurrentBars[1])
					continue;                                  // frisch angelegte Zone nicht sofort pruefen
				if (z.Height <= 0) { zones.RemoveAt(i); continue; }

				double penetration = 0;
				if (low < z.Top && high > z.Bottom)            // Kerze ueberlappt die Zone
				{
					double reachTop    = Math.Min(high, z.Top);
					double reachBottom = Math.Max(low,  z.Bottom);
					penetration = (reachTop - reachBottom) / z.Height;
				}
				if (penetration >= RecoverPct)
					zones.RemoveAt(i);
				else if (ZoneMaxAgeBars > 0 && CurrentBars[1] - z.CreatedBar > ZoneMaxAgeBars)
					zones.RemoveAt(i);
			}

			// Schritt 2: Neue Vector-Zone? Volumen- und Koerperbedingung auf der Zonenserie.
			if (CurrentBars[1] < ZoneVolLookback + 1 || CurrentBars[1] < ZoneAtrPeriod + 1)
				return;
			double avgVol = zoneVolAvg[1];                     // Durchschnitt OHNE die aktuelle Kerze
			double atrZ   = zoneAtr[1];
			if (avgVol <= 0 || atrZ <= 0)
				return;

			double body = Math.Abs(close - open);
			if (Volumes[1][0] >= ZoneVolMultiple * avgVol && body >= ZoneBodyAtrMult * atrZ)
			{
				zones.Add(new Zone
				{
					Top        = Math.Max(open, close),
					Bottom     = Math.Min(open, close),
					CreatedBar = CurrentBars[1],
				});
				if (zones.Count > 100)
					zones.RemoveAt(0);
				if (EnableDebugLog)
					Print(Times[1][0].ToString("yyyy-MM-dd HH:mm") + " MGZ: neue Zone "
						+ Math.Min(open, close) + " - " + Math.Max(open, close)
						+ " (aktiv: " + zones.Count + ")");
			}
		}

		// Naechstes Ziel oberhalb/unterhalb des Preises: Zonenkante oder ungetestetes PDH/PDL.
		// Liefert 0, wenn es in dieser Richtung keinen Magneten gibt.
		private double NearestTarget(bool above, double price)
		{
			double best = 0;
			foreach (Zone z in zones)
			{
				if (above && z.Bottom > price)
				{
					if (best == 0 || z.Bottom < best) best = z.Bottom;
				}
				else if (!above && z.Top < price)
				{
					if (best == 0 || z.Top > best) best = z.Top;
				}
			}
			if (UsePdTargets && CurrentBars.Length > 2 && CurrentBars[2] >= 1)
			{
				double pdh = Highs[2][0];
				double pdl = Lows[2][0];
				if (above && !pdhTested && pdh > price && (best == 0 || pdh < best)) best = pdh;
				if (!above && !pdlTested && pdl < price && (best == 0 || pdl > best)) best = pdl;
			}
			return best;
		}

		protected override void OnBarUpdate()
		{
			// ---------- 1-Tick-Serie: Delta akkumulieren ----------
			if (BarsInProgress == 3)
			{
				double p = Closes[3][0];
				double v = Volumes[3][0];
				int dir = p > tickLast ? 1 : p < tickLast ? -1 : tickDir;
				if (tickLast > 0 && dir != 0)
				{
					curDelta += dir * v;
					tickDir   = dir;
				}
				tickLast = p;
				return;
			}

			// ---------- Zonenserie: Zonen pflegen ----------
			if (BarsInProgress == 1)
			{
				if (CurrentBars[1] >= 1)
					UpdateZones();
				return;
			}

			if (BarsInProgress != 0)
				return;

			// Delta der soeben geschlossenen Chartkerze festschreiben
			barDelta = curDelta;
			curDelta = 0;
			deltaHistory.Add(barDelta);
			if (deltaHistory.Count > 1000)
				deltaHistory.RemoveRange(0, deltaHistory.Count - 500);

			if (CurrentBars[0] < BarsRequiredToTrade)
				return;
			if (CurrentBars.Length < 4 || CurrentBars[1] < 1 || CurrentBars[2] < 1)
				return;

			// ---------- Tageswechsel: PDH/PDL-Test-Status zuruecksetzen ----------
			if (Time[0].Date != currentDay)
			{
				currentDay  = Time[0].Date;
				tradesToday = 0;
				pdhTested   = false;
				pdlTested   = false;
			}
			double pdh = Highs[2][0];
			double pdl = Lows[2][0];
			if (pdh > 0 && High[0] >= pdh) pdhTested = true;
			if (pdl > 0 && Low[0]  <= pdl) pdlTested = true;

			// ---------- Handelsfreigabe ----------
			if (!IsTradingDay(Time[0].DayOfWeek))
				return;
			if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
				return;
			if (Position.MarketPosition != MarketPosition.Flat)
				return;
			if (UseTimeWindow)
			{
				TimeSpan bc = Time[0].TimeOfDay;
				if (bc <= new TimeSpan(StartHour, StartMinute, 0) || bc >= new TimeSpan(EndHour, EndMinute, 0))
					return;
			}

			// ---------- Brodeln: Delta-Summe der letzten DeltaWindow Kerzen ----------
			if (deltaHistory.Count < DeltaLookback + DeltaWindow)
				return;

			double sum = 0;
			for (int i = deltaHistory.Count - DeltaWindow; i < deltaHistory.Count; i++)
				sum += deltaHistory[i];

			double avgAbs = 0;
			for (int i = deltaHistory.Count - DeltaWindow - DeltaLookback; i < deltaHistory.Count - DeltaWindow; i++)
				avgAbs += Math.Abs(deltaHistory[i]);
			avgAbs /= DeltaLookback;
			if (avgAbs <= 0)
				return;

			bool brodelnLong  = sum >=  DeltaSumMultiple * avgAbs;
			bool brodelnShort = sum <= -DeltaSumMultiple * avgAbs;
			if (!brodelnLong && !brodelnShort)
				return;

			// ---------- Kompression: Druck muss aus einer Verdichtung kommen ----------
			if (UseCompression)
			{
				if (trigAtrAvg[0] <= 0 || trigAtr[0] > CompressionFactor * trigAtrAvg[0])
					return;
			}

			// ---------- Ziel suchen und Geometrie pruefen ----------
			bool   isLong = brodelnLong;
			if (isLong && !AllowLong)  return;
			if (!isLong && !AllowShort) return;

			double target = NearestTarget(isLong, Close[0]);
			if (target <= 0)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " MGZ: Brodeln "
						+ (isLong ? "LONG" : "SHORT") + ", aber kein Magnet in Richtung");
				return;
			}
			target = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? target - TargetBufferTicks * TickSize : target + TargetBufferTicks * TickSize);

			double stopDist = StopAtrMultiple * trigAtr[0];
			if (stopDist <= 0)
				return;
			if (MinStopPct > 0 && stopDist < Close[0] * MinStopPct / 100.0)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " MGZ: verworfen — Stop "
						+ Math.Round(stopDist / Close[0] * 100, 3) + " % < MinStopPct");
				return;
			}

			double dist = isLong ? target - Close[0] : Close[0] - target;
			if (dist <= 0 || dist / stopDist < MinRR)
			{
				if (EnableDebugLog)
					Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " MGZ: verworfen — RR "
						+ Math.Round(dist / stopDist, 2) + " < " + MinRR
						+ " (Ziel " + target + ")");
				return;
			}

			int qty = CalcQuantity(stopDist);
			if (qty < 1)
				return;

			double stop = Instrument.MasterInstrument.RoundToTickSize(
				isLong ? Close[0] - stopDist : Close[0] + stopDist);

			// Stop und Ziel sind Markt-Struktur-Level: Sie bleiben absolut stehen und
			// werden NICHT auf den Fill nachgerechnet — das Ziel ist die These.
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
				Print(Time[0].ToString("yyyy-MM-dd HH:mm") + " MGZ: " + (isLong ? "LONG " : "SHORT ") + qty
					+ " @ " + Close[0] + " | Ziel " + target + " | Stop " + stop
					+ " | RR " + Math.Round(dist / stopDist, 2)
					+ " | DeltaSum " + Math.Round(sum, 0) + " (x" + Math.Round(sum / avgAbs, 1) + ")"
					+ " | Trade " + tradesToday + " heute");
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Zeitfenster aktiv", Description = "AUS (Standard): ganztaegig. AN: Einstiege nur zwischen Start und Ende.", Order = 1, GroupName = "01 Zeiten")]
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
		[Range(2, 1440)]
		[Display(Name = "Zonen-Timeframe (Minuten)", Description = "Auf dieser Serie werden die Vector-Zonen erkannt. Standard 15.", Order = 20, GroupName = "03 Ziele (Magneten)")]
		public int ZoneTfMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 20)]
		[Display(Name = "Zone: Volumen-Faktor", Description = "Volumen der Zonenkerze >= Faktor x Durchschnitt der vorhergehenden Kerzen. Standard 2.", Order = 21, GroupName = "03 Ziele (Magneten)")]
		public double ZoneVolMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Zone: Volumen-Durchschnitt ueber", Description = "Kerzen fuer den Volumendurchschnitt (ohne die Zonenkerze). Standard 20.", Order = 22, GroupName = "03 Ziele (Magneten)")]
		public int ZoneVolLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 10)]
		[Display(Name = "Zone: Koerper (x ATR)", Description = "Koerper der Zonenkerze >= Faktor x ATR der Zonenserie. Standard 1,5.", Order = 23, GroupName = "03 Ziele (Magneten)")]
		public double ZoneBodyAtrMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "Zone: ATR-Periode", Order = 24, GroupName = "03 Ziele (Magneten)")]
		public int ZoneAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.05, 1)]
		[Display(Name = "Recovered ab Fuellung", Description = "Zone verfaellt, wenn der Preis ihren Koerper zu diesem Anteil gefuellt hat. 0,5 = 50 % (Standard), 1 = komplette Durchquerung noetig.", Order = 25, GroupName = "03 Ziele (Magneten)")]
		public double RecoverPct { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10000)]
		[Display(Name = "Zone: max. Alter (Kerzen)", Description = "0 = unbegrenzt (Standard). Sonst verfaellt die Zone nach so vielen Zonen-Kerzen.", Order = 26, GroupName = "03 Ziele (Magneten)")]
		public int ZoneMaxAgeBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vortageslevel als Ziele", Description = "AN (Standard): ungetestete PDH/PDL zaehlen als Magneten.", Order = 27, GroupName = "03 Ziele (Magneten)")]
		public bool UsePdTargets { get; set; }

		[NinjaScriptProperty]
		[Range(0, 200)]
		[Display(Name = "Ziel-Puffer (Ticks)", Description = "Das Profit-Ziel liegt so viele Ticks VOR dem Magneten. Standard 4.", Order = 28, GroupName = "03 Ziele (Magneten)")]
		public int TargetBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Brodeln: Fenster (Kerzen)", Description = "Ueber so viele Kerzen (inkl. der aktuellen) wird das Delta summiert. Standard 5.", Order = 30, GroupName = "04 Brodeln (Trigger)")]
		public int DeltaWindow { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 50)]
		[Display(Name = "Brodeln: Delta-Summen-Faktor", Description = "Die Delta-Summe muss mindestens Faktor x das Durchschnitts-|Delta| einer einzelnen Kerze erreichen. Standard 2.", Order = 31, GroupName = "04 Brodeln (Trigger)")]
		public double DeltaSumMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "Brodeln: Delta-Durchschnitt ueber", Description = "Kerzen fuer das Durchschnitts-|Delta| (vor dem Fenster). Standard 20.", Order = 32, GroupName = "04 Brodeln (Trigger)")]
		public int DeltaLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Kompression noetig", Description = "AN (Standard): Der Delta-Schub zaehlt nur, wenn der aktuelle ATR unter Faktor x langem ATR-Durchschnitt liegt (Druck aus der Verdichtung).", Order = 33, GroupName = "04 Brodeln (Trigger)")]
		public bool UseCompression { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 2)]
		[Display(Name = "Kompression: Faktor", Description = "ATR muss unter Faktor x ATR-Durchschnitt liegen. Standard 0,85.", Order = 34, GroupName = "04 Brodeln (Trigger)")]
		public double CompressionFactor { get; set; }

		[NinjaScriptProperty]
		[Range(10, 2000)]
		[Display(Name = "Kompression: Durchschnitt ueber", Description = "Kerzen fuer den langen ATR-Durchschnitt. Standard 100.", Order = 35, GroupName = "04 Brodeln (Trigger)")]
		public int CompressionAvgPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "Stop (x ATR)", Description = "Stopdistanz = Faktor x ATR der Chartserie. Standard 1,5.", Order = 40, GroupName = "05 Risiko")]
		public double StopAtrMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "ATR-Periode (Chartserie)", Order = 41, GroupName = "05 Risiko")]
		public int TrigAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 50)]
		[Display(Name = "Mindest-RR", Description = "Einstieg nur, wenn Distanz zum Magneten / Stopdistanz >= diesem Wert. Standard 2.", Order = 42, GroupName = "05 Risiko")]
		public double MinRR { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10)]
		[Display(Name = "Mindest-Stop (% vom Preis)", Description = "0 = aus (Standard). Sonst muss die Stopdistanz mindestens diesen Prozentsatz des Preises betragen — Gebuehren-Schutz fuer Perp-Handel (z. B. 0,3).", Order = 43, GroupName = "05 Risiko")]
		public double MinStopPct { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Groesse aus Geldrisiko", Description = "True (Standard): Kontraktzahl so, dass ein Stopout etwa dem Betrag unten entspricht.", Order = 44, GroupName = "05 Risiko")]
		public bool UseFixedRisk { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000000)]
		[Display(Name = "Risiko je Trade", Order = 45, GroupName = "05 Risiko")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max. Kontrakte", Order = 46, GroupName = "05 Risiko")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Kontrakte (fest)", Description = "Nur wenn 'Groesse aus Geldrisiko' aus ist.", Order = 47, GroupName = "05 Risiko")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Long erlauben",  Order = 48, GroupName = "05 Risiko")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Short erlauben", Order = 49, GroupName = "05 Risiko")]
		public bool AllowShort { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Max. Trades pro Tag", Description = "0 = unbegrenzt (Standard).", Order = 50, GroupName = "06 Verhalten")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Debug-Log aktiv", Description = "Loggt neue Zonen, Einstiege und verworfene Signale mit Grund (kein Magnet, RR zu klein, Stop zu eng).", Order = 60, GroupName = "07 Diagnose")]
		public bool EnableDebugLog { get; set; }
		#endregion
	}
}
