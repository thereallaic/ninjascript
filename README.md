# NinjaTrader 8.1 Strategien — Backtest-Projekt

Repo für NinjaScript-Strategien, die im NinjaTrader 8.1 Strategy Analyzer gebacktestet werden.

## Strategien

| Datei | Name | Idee |
|---|---|---|
| `Strategies/OpeningPullback2R.cs` | OpeningPullback2R | Opening-Bias aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, Einstieg auf der ersten Kerze in Bias-Richtung, Stop auf dem Close der letzten Gegenkerze, Ziel = 2R |
| `Strategies/OpeningImmediate2R.cs` | OpeningImmediate2R | Wie oben, aber SOFORTIGER Einstieg direkt nach der 5. Kerze — kein Warten auf eine Signalkerze |
| `Strategies/OpeningPullbackSwing1R.cs` | OpeningPullbackSwing1R | Wie Variante 1, aber Stop unter dem geformten Swing-Low / über dem Swing-High (Wick statt Close) und Ziel = 1R |
| `Strategies/OpeningPullbackSwingReverse1R.cs` | OpeningPullbackSwingReverse1R | Umkehrung von Variante 3: gleiches Signal, gleicher Zeitpunkt, **gedrehte Orderrichtung** |
| `Strategies/VolumeSpikeEma50.cs` | VolumeSpikeEma50 | Eigenständiger Ansatz: Long **und** Short 12:00–22:00 (Mo–Do), Volumenausbruch (2×) mit EMA50-Richtungsfilter, Stop auf dem Candle-Open, frei einstellbares R-Ziel |

---

## OpeningPullback2R — Regelwerk

Instrument-Ziel: **FDXS 09-26** (Micro-DAX, Eurex). 1-Minuten-Kerzen.

1. **Referenz:** Open der 09:00-Kerze.
2. **Beobachtung:** Kerzen 09:00–09:05 (5 Stück). Kein Handel in diesem Fenster.
3. **Bias:** Close der 5. Kerze **über** dem 9:00-Open → Long-Bias. **Darunter** → Short-Bias. Exakt gleich → kein Trade.
4. **Einstieg Long:** erste **grüne** Kerze nach dem Fenster → Market-Order bei deren Schluss. **Short:** erste **rote** Kerze, spiegelbildlich.
5. **Stop Long:** Close der **zuletzt gesehenen roten Kerze** vor dem Einstieg, minus Offset. **Short:** Close der zuletzt gesehenen grünen Kerze, plus Offset. Die „letzte Gegenkerze" wird ab 09:00 fortlaufend mitgeführt — sie kann also aus dem 5er-Fenster oder aus den Wartekerzen danach stammen.
6. **Ziel:** R = |Einstieg − Stop|. Take-Profit = Einstieg ± 2R (nach Fill exakt auf den tatsächlichen Einstiegskurs berechnet).
7. **Sonstiges:** max. 1 Trade pro Tag · kein Einstieg nach Cutoff (Standard 10:00) · offene Position wird spätestens zum Sessionende geflattet.

> **Fill-Timing:** Bei `Calculate.OnBarClose` geht die Market-Order beim Schluss der Signalkerze raus und wird zum **Open der Folgekerze** gefüllt. Im Chart sieht der Einstiegspfeil deshalb immer eine Kerze „zu spät" aus. Das ist korrekt und realistisch — ein Fill exakt zum Schlusskurs der Signalkerze wäre in der Praxis nicht handelbar.

### Bewusste Festlegungen (per Parameter änderbar)

| Fall | Verhalten (Default) | Parameter |
|---|---|---|
| Keine Gegenkerze seit 09:00 vorhanden | Fallback: tiefster/höchster Close **aller** 5 Fensterkerzen als Stop-Basis | `AllowFallbackStop` |
| Signalkerze schließt bereits auf/jenseits des Stops (R ≤ 0) | Nächste Signalkerze abwarten | `StrictFirstSignal` |
| Doji (Close == Open) | Zählt weder als rote noch als grüne Kerze | — |
| Keine Signalkerze bis Cutoff | Kein Trade an diesem Tag | `CutoffHour/Minute` |

### Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `OpenHour` / `OpenMinute` | 9 / 0 | Referenz-Eröffnungszeit (lokale NT-Zeitzone!) |
| `InitialBars` | 5 | Anzahl Beobachtungskerzen |
| `CutoffHour` / `CutoffMinute` | 10 / 0 | Letzte mögliche Signalkerze schließt zu dieser Zeit |
| `RewardMultiple` | 2 | Take-Profit in R |
| `StopOffsetTicks` | 2 | Puffer unter/über der Stop-Basis. **0 = exakt auf dem Close** (FDXS: 1 Tick = 1 Punkt = 1 €) |
| `UseFixedRisk` | true | Positionsgröße aus dem Geldrisiko berechnen (siehe unten) |
| `RiskPerTrade` | 100 | Betrag, den 1R kosten darf — **in Instrumentenwährung** |
| `MaxContracts` | 50 | Obergrenze der berechneten Positionsgröße |
| `Contracts` | 1 | Feste Positionsgröße — nur aktiv wenn `UseFixedRisk = false` |
| `UseWindowExtremeStop` | false | false = Stop auf dem Close der letzten Gegenkerze · true = tiefster roter / höchster grüner Close der 5 Anfangskerzen (altes Verhalten, deutlich weitere Stops) |
| `MinRiskTicks` | 0 (aus) | Signale mit einem Stop-Abstand unter diesem Wert verwerfen — siehe Warnung unten |
| `AllowFallbackStop` | true | s. o. |
| `StrictFirstSignal` | false | s. o. |

### Positionsgrößen-Normierung (1R = fester Geldbetrag)

Mit `UseFixedRisk = true` (Standard) berechnet die Strategie die Kontraktzahl selbst:

```
Kontrakte = abrunden( RiskPerTrade / (R in Punkten × Punktwert) )
```

FDXS hat **1 Punkt = 1 €**, bei `RiskPerTrade = 100` also:

| R (Punkte) | Kontrakte | Tatsächliches Risiko |
|---|---|---|
| 10 | 10 | 100 € |
| 20 | 5 | 100 € |
| 42 | 2 | 84 € |
| 60 | 1 | 60 € |
| 101 | 0 → **Trade entfällt** | — |

Warum das den Backtest überhaupt erst aussagekräftig macht: Bei fester Kontraktzahl bestimmt die zufällige Stop-Distanz, wie viel ein Trade wiegt — ein Gewinner mit 40 Punkten R zählt viermal so viel wie einer mit 10. Die Equity-Kurve misst dann Stop-Distanzen statt Signalqualität. Mit fixem Geldrisiko wiegt jeder Trade gleich, und der Profit Factor sagt tatsächlich etwas über den Edge aus.

**Drei Dinge, die du wissen musst:**

1. **Währung:** `RiskPerTrade` ist in der Währung des Instruments. FDXS notiert in **EUR** — 100 bedeutet also 100 €, nicht 100 $. Willst du wirklich 100 US-Dollar, trag beim aktuellen Kurs rund 86 ein.
2. **Abrundung:** Es gibt nur ganze Kontrakte. Bei großem R wird das tatsächliche Risiko spürbar kleiner als 100 € (siehe Tabelle). Dass FDXS nur 1 €/Punkt hat, ist hier ein Vorteil — beim großen FDAX (25 €/Punkt) wäre die Normierung praktisch unmöglich.
3. **Übergroßes R:** Ergibt die Rechnung 0 Kontrakte (R > 100 Punkte bei 100 € Risiko), wird der Trade **ausgelassen**. Das ist gewollt — die Alternative wäre, das Risikolimit zu überschreiten. Behalte im Blick, wie oft das passiert: Wenn regelmäßig Tage wegfallen, ist entweder `RiskPerTrade` zu niedrig oder die Stop-Logik zu weit.

> **⚠️ Achtung, zu enge Stops:** Der Stop auf dem Close der letzten Gegenkerze kann bei ruhigen Minuten nur wenige Punkte entfernt liegen. R wird dann winzig, das 2R-Ziel liegt in Rauschweite, und Kommission plus Slippage fressen den Trade auf — bei FDXS können 1–2 Punkte Gebühren einen 4-Punkte-R komplett neutralisieren. Nach dem ersten Backtest die Spalte **MAE/Entry-Distanz** im Trades-Tab prüfen: Wenn viele Trades ein R unter ~10 Punkten haben, `MinRiskTicks` auf 8–15 setzen und erneut laufen lassen.

> **Achtung Zeitzone:** Die 9:00-Logik greift auf die in NinjaTrader eingestellte Zeitzone zu. Prüfen unter **Tools → Options → General → Time zone** → muss `(UTC+01:00) Amsterdam, Berlin, …` sein.

---

## OpeningImmediate2R — Variante 2 (Sofort-Einstieg)

Identisch zu OpeningPullback2R bis auf den Einstieg: **kein Warten auf eine Signalkerze.** Der Markteinstieg erfolgt direkt nach dem Richtungsentscheid.

**Timing-Detail:** Die 5. Kerze schließt um 09:05:00 — der „sofortige" Einstieg füllt daher um **09:05:00** (Open der Folgeminute), nicht 09:06. Wer den Fill exakt um 09:06:00 will, setzt `EntryDelayBars = 1` (dann wird eine Kerze — egal welcher Farbe — abgewartet).

Stop- und Ziel-Logik identisch zu Variante 1: Stop = Close der zuletzt gesehenen Gegenkerze im Fenster (rot bei Long, grün bei Short) ± Offset, Take-Profit = Einstieg ± 2R auf Basis des tatsächlichen Fills. Dadurch unterscheiden sich die beiden Strategien **nur** im Einstiegszeitpunkt — genau das macht den Vergleich aussagekräftig.

**Zusätzliche/entfallene Parameter gegenüber Variante 1:**

| Parameter | Default | Bedeutung |
|---|---|---|
| `EntryDelayBars` | 0 | 0 = Order beim Schluss der 5. Kerze (Fill 09:05:00) · 1 = eine Kerze später (Fill 09:06:00) |
| `MinRiskTicks` | 0 (aus) | wie Variante 1 |
| `UseFixedRisk` / `RiskPerTrade` / `MaxContracts` | true / 100 / 50 | wie Variante 1 |
| ~~`CutoffHour/Minute`~~ | — | entfällt (Einstieg ist deterministisch, kein Warten) |
| ~~`StrictFirstSignal`~~ | — | entfällt (kein Signalkerzen-Konzept) |

Randfall: Schließt die 5. Kerze bereits auf/jenseits des berechneten Stops (R ≤ 0), findet kein Trade statt.

### Wenn keine Trades ausgeführt werden

`OpeningImmediate2R` hat ein **Debug-Log** (`EnableDebugLog`, Standard `true`). Öffne im NinjaTrader-Hauptfenster **New → NinjaScript Output** *bevor* du den Backtest startest — dort erscheint danach für jeden Tag eine Zeile: entweder eine ausgeführte Order oder der genaue Grund, warum sie ausgelassen wurde (kein Bias, keine Stop-Basis, R zu klein, oder Kontraktzahl 0).

**Häufigste Ursache für „keine Trades bei allen Tagen": PointValue = 0.** Seit `UseFixedRisk = true` Standard ist, berechnet die Strategie die Kontraktzahl aus `RiskPerTrade / (R × PointValue)`. Ist der **Point Value des Instruments in NinjaTrader nicht hinterlegt** (0 oder leer — kommt bei frisch importierten Kontraktmonaten wie SEP26 vor), ist die Kontraktzahl für **jeden** Trade 0, und er wird stillschweigend übersprungen — bei allen vier Strategien in diesem Repo, nicht nur bei dieser. Die Log-Zeile beim Start (`PointValue=...`) und die `LogLevel.Error`-Meldung im **Log**-Tab zeigen das direkt an.

**Schnelltest:** `UseFixedRisk` testweise auf `false` stellen und neu laufen lassen. Kommen dann Trades, war es exakt das — Point Value unter **Control Center → Tools → Instruments** für FDXS SEP26 auf `1` setzen (1 Punkt = 1 €) und `UseFixedRisk` wieder auf `true`.

---

## OpeningPullbackSwing1R — Variante 3 (Swing-Stop, 1R-Ziel)

Einstiegslogik **identisch zu Variante 1** (Bias aus den 5 Anfangskerzen, Long auf der ersten grünen Kerze danach, Short auf der ersten roten). Zwei Unterschiede:

**1. Stop unter dem geformten Swing-Extrem statt auf einem Close.** Ab dem Ende des 5er-Fensters wird das tiefste Low (Long) bzw. höchste High (Short) aller Kerzen mitgeführt — **einschließlich der Signalkerze selbst**. Der Stop liegt `StopOffsetTicks` darunter bzw. darüber.

```
Long:   Stop = tiefstes Low  des Pullbacks − Offset
Short:  Stop = höchstes High des Pullbacks + Offset
```

Das ist die strukturell sauberere Platzierung: Der Stop sitzt unter dem Docht, nicht im Kerzenkörper. Ein Wick-Test des Pullback-Tiefs stoppt dich damit nicht mehr aus.

**2. Take-Profit = 1R** statt 2R (`RewardMultiple = 1`).

### Was das für das Profil bedeutet

R ist hier **systematisch größer** als in Variante 1 — der Docht liegt naturgemäß unter dem Close. Zusammen mit dem 1R-Ziel ergibt das ein völlig anderes Profil:

| | Variante 1 (Close-Stop, 2R) | Variante 3 (Swing-Stop, 1R) |
|---|---|---|
| R-Distanz | eng | weiter |
| Nötige Trefferquote (vor Kosten) | > 33 % | > 50 % |
| Stopouts durch Wick-Tests | häufig | selten |
| Kontraktzahl bei 100 € Risiko | hoch | niedriger |
| Kosten je Trade relativ zu R | hoch | niedriger |

Genau dieser Trade-off ist der interessante Vergleich: Variante 1 braucht wenige große Gewinner, Variante 3 braucht konstante Treffer. Welche gewinnt, hängt davon ab, ob der FDXS nach dem Opening wirklich durchläuft oder eher zappelt.

### Abweichende Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `RewardMultiple` | **1** | Take-Profit in R (in Variante 1: 2) |
| `StopOffsetTicks` | 2 | Puffer unter dem Swing-Low / über dem Swing-High |
| `IncludeWindowInSwing` | false | false = nur Kerzen **nach** dem 5er-Fenster bilden den Swing · true = die 5 Anfangskerzen zählen mit (deutlich weitere Stops, wenn die 9:00-Kerze einen langen Docht hat — genau das Problem, das Variante 1 ursprünglich hatte) |
| ~~`UseWindowExtremeStop`~~ | — | entfällt, durch `IncludeWindowInSwing` ersetzt |
| ~~`AllowFallbackStop`~~ | — | entfällt (ein Low/High existiert immer, kein Fallback nötig) |

Alle übrigen Parameter (Zeiten, `UseFixedRisk`, `RiskPerTrade`, `MaxContracts`, `MinRiskTicks`, `StrictFirstSignal`) verhalten sich wie in Variante 1.

> Die Warnung zu Mini-Stops aus Variante 1 gilt hier deutlich schwächer — durch den Docht-Abstand fällt R selten unter ~5 Punkte. `MinRiskTicks` kann meist auf 0 bleiben.

---

## OpeningPullbackSwingReverse1R — Variante 4 (Umkehrung von Variante 3)

Testet die Gegenhypothese: **Ist das Signal aus Variante 3 in Wahrheit ein Kontra-Indikator?**

Alles bleibt identisch — Richtungsentscheid aus den 5 Anfangskerzen, Signalkerze, Einstiegszeitpunkt, Positionsgrößenlogik. Gedreht wird nur die Orderrichtung:

| Bias (Close 5. Kerze vs. 9:00-Open) | Auslöser | Variante 3 | Variante 4 |
|---|---|---|---|
| über dem Open | erste **grüne** Kerze | LONG | **SHORT** |
| unter dem Open | erste **rote** Kerze | SHORT | **LONG** |

Der Auslöser bleibt bewusst unverändert: Bei Long-Bias wird weiterhin auf die erste grüne Kerze gewartet — nur wird dort jetzt geshortet statt gekauft. Dadurch handeln beide Varianten **exakt dieselben Tage zur exakt selben Minute**, und der Vergleich isoliert allein die Richtung.

### Die zwei Stop-Modi

**Spiegel-Modus (Standard, `UseStructuralStop = false`)** — R wird von Variante 3 übernommen und nur um den Einstieg geklappt:

```
Long-Bias, Signalkerze schließt bei C, Swing-Low bei L, Offset O:
  Variante 3:  Stop = L − O          R = C − (L − O)     Ziel = C + 1R
  Variante 4:  Stop = C + R                              Ziel = C − 1R
```

Konkret bei C = 26280, L = 26260, O = 2: R = 22 Punkte. Variante 3 hat Stop 26258 / Ziel 26302 — Variante 4 hat Stop 26302 / Ziel 26258. **Jeder Trade ist das fotografische Negativ:** Was in Variante 3 ins Ziel lief, läuft hier in den Stop und umgekehrt. Läuft Variante 3 auf 40 % Trefferquote, muss Variante 4 auf rund 60 % kommen.

> Das gilt exakt nur bei `RewardMultiple = 1`, weil Stop und Ziel dann gleich weit entfernt sind. Stellst du auf 2R um, bleibt der Stop bei 1R stehen und die Spiegelung ist keine saubere Umkehr mehr.

**⚠️ Wichtig für die Auswertung — Netto-P&L ist NICHT die exakte Umkehr:** Brutto (vor Kosten) heben sich beide Varianten trade-für-trade exakt auf — die Summe ihrer Brutto-P&L über alle Tage ist zwingend null. Kommission und Slippage werden dabei aber nicht mitgespiegelt, sondern fallen bei beiden Richtungen gleich an:

```
V4_netto = −V3_netto − 2 × Gesamtkosten
```

Verliert Variante 3 also netto 500 € (davon 50 € Kosten), gewinnt Variante 4 nicht netto 500 €, sondern nur **400 €** — die eigenen 50 € Kosten werden nochmal fällig. Sind beide Varianten in eurem Backtest leicht negativ, ist das also kein Widerspruch, sondern bei engen Stops und viel Volumen der Normalfall: Erst wenn eine Seite um deutlich mehr als 2×Gesamtkosten verliert, steckt darin echte Richtungsinformation statt nur Transaktionskosten.

**Struktureller Modus (`UseStructuralStop = true`)** — der Stop sitzt am Swing-Extrem der Gegenseite (beim Short also über dem Swing-High). Handelstechnisch die sinnvollere Platzierung, aber R und damit die Positionsgröße weichen von Variante 3 ab, es ist also kein exakter Umkehrtest mehr, sondern eine eigenständige Strategie.

### Was das Ergebnis bedeutet

- **Beide verlieren** → das Signal trägt keine Information, die Kosten fressen beide Seiten. Häufigster Fall, und ein klares Stopp-Signal für diesen Ansatz.
- **Variante 4 gewinnt deutlich** → der Opening-Pullback ist am FDXS ein Fade-Setup. Erst gegen einen zweiten Zeitraum (Out-of-Sample) prüfen, bevor du das glaubst.
- **Beide etwa bei null** → Rauschen, mehr Handelstage nötig.

Alle Parameter entsprechen Variante 3, zusätzlich nur `UseStructuralStop`.

---

## VolumeSpikeEma50 — Volumen-Ausbruch mit EMA50-Filter (eigenständiger Ansatz)

Kein Opening-Setup, sondern eine eigene Idee: **Ein Volumenausbruch in Richtung des Trends läuft weiter.** Der EMA50 liefert die Trendrichtung, das Volumen den Auslöser.

1. **Handelsfenster:** 12:00–22:00, **Montag bis Donnerstag**. Außerhalb passiert nichts.
2. **Signalkerze:** Volumen ≥ **2,0 ×** Durchschnittsvolumen der 20 vorhergehenden Kerzen.
3. **Richtung:** Close **über** EMA50 → Long · Close **unter** EMA50 → Short.
4. **Stop = Open der Signalkerze.**
5. **Positionsgröße** so, dass ein Stopout ungefähr **100 €** kostet.
6. **Ziel = `RewardMultiple` × Stopdistanz** — der frei einstellbare R-Wert, Standard 1.
7. Immer nur **eine Position gleichzeitig**. Offene Position wird um **22:00 glattgestellt**.

> **Sperrzeiten:** Die Vorgabe „keine Trades von 09:00 bis 12:00" ist über den **Fensterstart** abgebildet (`StartHour = 12`), nicht über eine eigene Blackout-Mechanik — bei einem Fenster, das ohnehin um 09:00 beginnen würde, ist beides identisch. Der Freitagsfilter (`TradeFriday = false`) sperrt nur **neue Einstiege**; er steht im Code nach der Glattstellungs-Logik, damit eine offene Position in jedem Fall regulär beendet würde.

### Risikobudget: 1 % vom mitwachsenden Konto

Das Risiko je Trade ist **prozentual an den Kontostand gekoppelt** und wächst bzw. schrumpft mit ihm:

```
Equity  = StartingCapital + realisierte Performance der Strategie
Budget  = Equity × RiskPercent / 100
```

Bei 10.000 Start und 1 % ist der erste Trade also **100 €** — identisch zum bisherigen Festbetrag, danach compoundet es. Bei 12.000 sind es 120 €, bei 8.000 nur noch 80 €.

Als Equity dient bewusst `StartingCapital + SystemPerformance…CumProfit` und **nicht** `Account.Get(AccountItem.CashValue)`: Der Account-Zugriff ist im Strategy Analyzer nicht verlässlich, die SystemPerformance funktioniert in Backtest und Realtime identisch. Unrealisierte Gewinne der offenen Position zählen nicht mit — gesized wird ohnehin nur im flachen Zustand.

> **Stell die Account-Größe im Strategy Analyzer ebenfalls auf 10.000**, sonst passen dessen Prozentkennzahlen (Return, Drawdown %) nicht zur internen Rechnung der Strategie.

Über `UsePercentRisk = false` schaltest du auf den festen Betrag aus `RiskAmount` zurück.

### Positionsgröße und der Kappungsfall

Die **Stopdistanz ist durch die Kerze vorgegeben**, die **Kontraktzahl wird daraus berechnet**:

```
Stopdistanz = |Close der Signalkerze − Open der Signalkerze|
Kontrakte   = abrunden( Budget / (Stopdistanz × PointValue) )
```

Abgerundet wird bewusst: Das Risiko liegt damit nie *über* dem Budget, bei weiten Stops aber spürbar darunter. Beispiele für FDXS (1 Punkt = 1 €) bei 10.000 Equity, Budget 100 €:

| Signalkerze | Stopdistanz | Kontrakte | Risiko | effektiv | Stop liegt auf |
|---|---|---|---|---|---|
| Open 26250 → Close 26270 | 20 Pkt | 5 | 100 € | 1,00 % | Candle-Open |
| Open 26250 → Close 26256 | 6 Pkt | 16 | 96 € | 0,96 % | Candle-Open |
| Open 26250 → Close 26290 | 40 Pkt | 2 | 80 € | 0,80 % | Candle-Open |
| Open 26250 → Close 26301 | 51 Pkt | 1 | 51 € | 0,51 % | Candle-Open |
| Open 26100 → Close 26250 | 150 Pkt | 1 *(gekappt)* | 100 € | 1,00 % | 100 Pkt unter Entry |

**Die Abrundung macht 1 % zur Obergrenze, nicht zum Zielwert.** Bei kleinem Konto und weiten Stops liegt das tatsächliche Risiko regelmäßig darunter — im Extremfall bei gut der Hälfte, wenn `Budget / Stopdistanz` knapp unter 2 fällt. Das Debug-Log gibt bei jedem Trade das effektive Prozent aus. Mit wachsendem Konto wird die Abstufung feiner und der Effekt verschwindet weitgehend.

**Kappungsfall:** Ist die Stopdistanz so groß, dass selbst 1 Kontrakt mehr als das Budget riskieren würde, wird 1 Kontrakt gehandelt und der Stop auf genau das Budget **herangezogen**. Er liegt dann nicht mehr auf dem Candle-Open, sondern auf dem Geldlimit — aus einem strukturellen wird ein geldbasierter Stop. Das Debug-Log markiert diese Trades mit `GEKAPPT`.

> **⚠️ `MaxContracts` bremst bei wachsendem Konto.** Die Grenze von 50 Kontrakten greift, sobald `Budget / Stopdistanz > 50` wird — bei `MinStopTicks = 10` also ab rund **50.000 € Equity**. Ab da riskiert die Strategie stillschweigend weniger als 1 %, und die Compounding-Kurve flacht künstlich ab. Wenn dein Backtest so weit wächst, `MaxContracts` entsprechend hochsetzen — oder bewusst dort belassen, wenn 50 Micro-Kontrakte deine realistische Liquiditätsgrenze sind.

### Zwei Konsequenzen der Regeln, die du kennen solltest

**Der Stop auf dem Candle-Open erzwingt implizit die Kerzenfarbe.** Bei Long muss der Stop unter dem Einstieg liegen, also Open < Close → grüne Kerze. Eine rote Kerze über dem EMA50 mit 2× Volumen hätte ihren Stop *über* dem Einstieg und wird deshalb übersprungen (Debug-Log: „Stop auf falscher Seite"). Long handelt faktisch nur grüne Kerzen, Short nur rote.

### Warum `MinStopTicks` wichtiger ist, als es aussieht

Bei festem Geldrisiko wächst die Positionsgröße **invers zur Stopdistanz**. Die Kosten fallen aber **pro Kontrakt** an — ein enger Stop bedeutet also nicht nur mehr Kontrakte, sondern proportional mehr Gebühren und Slippage bei unverändertem 1R.

Mit ~2,50 € Reibung je Kontrakt und Round-Turn (Kommission + je 1 Tick Slippage bei Ein- und Ausstieg) und 100 € Risiko:

| Stopdistanz | Kontrakte | Reibung gesamt | Anteil an 1R | Nötige Trefferquote bei 1R |
|---|---|---|---|---|
| 5 Pkt | 20 | 50 € | 50 % | 75 % |
| 10 Pkt | 10 | 25 € | 25 % | 63 % |
| 15 Pkt | 6 | 15 € | 15 % | 58 % |
| 25 Pkt | 4 | 10 € | 10 % | 55 % |
| 50 Pkt | 2 | 5 € | 5 % | 53 % |

Ein 5-Punkte-Stop verlangt also 75 % Trefferquote, nur um bei ±0 herauszukommen — chancenlos. Deshalb steht `MinStopTicks` auf **10** statt auf 0.

**Warum 10 und nicht höher:** 10 ist als *Pathologie-Filter* gesetzt, nicht als Optimierung. Er entfernt die kaputten Fälle (Stop im Spread und im Minutenrauschen), greift aber noch nicht stark in deine Regeln ein. Ökonomisch wären 20–25 besser, das kostet aber spürbar Signale. **Prüfe im ersten Durchlauf die Verteilung der Stopdistanzen** im Trades-Tab bzw. im Debug-Log — dann weißt du, was ein höherer Wert wirklich kostet, statt es zu schätzen. Das ist genau die Art Entscheidung, die aus den Daten kommen sollte und nicht aus einem Bauchgefühl.

> Nebeneffekt: `MinStopTicks` deckelt indirekt die Positionsgröße auf `RiskAmount / (MinStopTicks × PointValue)` — bei 10 also auf 10 Kontrakte. `MaxContracts = 50` wird damit nie erreicht und ist nur noch ein Sicherheitsnetz, falls du den Filter absenkst.

### Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `StartHour` / `StartMinute` | **12** / 0 | Beginn des Handelsfensters — bildet die Sperre 09:00–12:00 ab |
| `EndHour` / `EndMinute` | 22 / 0 | Ende — danach keine neuen Einstiege |
| `CloseAtWindowEnd` | true | Offene Position um 22:00 schließen · false = bis Stop/Ziel laufen lassen |
| `TradeFriday` | **false** | Freitags keine neuen Einstiege |
| `EmaPeriod` | 50 | Periode des Richtungsfilters |
| `VolumeMultiple` | 2.0 | Ab welchem Vielfachen des Durchschnitts eine Kerze als Ausbruch zählt |
| `VolumeLookback` | 20 | Anzahl Kerzen für den Durchschnitt — **ohne** die aktuelle Kerze |
| `EnableLong` / `EnableShort` | true / true | Richtungen einzeln abschaltbar, um sie isoliert zu testen |
| `UsePercentRisk` | **true** | 1R = Prozent vom mitwachsenden Konto · false = fester Betrag |
| `StartingCapital` | **10000** | Ausgangskontostand — mit der Account-Größe im Analyzer abgleichen |
| `RiskPercent` | **1.0** | Anteil des Kontostands je Stopout |
| `RiskAmount` | 100 | Fester Geldbetrag — nur aktiv wenn `UsePercentRisk = false` |
| `RewardMultiple` | **1** | **Der R-Wert zum Durchtesten.** Ziel = Multiple × Stopdistanz |
| `MaxContracts` | 50 | Obergrenze der berechneten Positionsgröße |
| `MinStopTicks` | **10** | Signale mit kleinerer Stopdistanz verwerfen — siehe Kostenrechnung oben. 0 = aus |
| `MaxTradesPerDay` | 0 | 0 = unbegrenzt |
| `EnableDebugLog` | false | Loggt Signal, Volumenverhältnis, EMA, Stopdistanz, Risiko, Kappung, Fill und Zeit-Exit |

### Details, die das Ergebnis beeinflussen

- **Der Volumen-Durchschnitt schließt die Signalkerze aus** (`SMA(Volume, 20)[1]`). Sonst würde eine Volumenspitze ihren eigenen Schwellwert nach oben ziehen und das Signal systematisch abschwächen.
- **„Über dem EMA50" ist als Close > EMA50 umgesetzt** — nicht als „gesamte Kerze inklusive Docht oberhalb".
- **Zeitstempel-Logik:** Die Kerze, die um 12:00 schließt, enthält noch Daten von vor 12:00 und zählt nicht zum Fenster. Erste mögliche Signalkerze schließt um 12:01.
- **Compounding verändert die Auswertung.** Mit prozentualem Risiko wird das Ergebnis **pfadabhängig**: Ein früher Gewinn vergrößert alle folgenden Positionen, ein früher Verlust verkleinert sie. Dieselben Trades in anderer Reihenfolge ergeben ein anderes Endkapital. Net Profit wird dadurch exponentiell und reagiert stark auf Zufälle am Anfang der Kurve. Für den **Vergleich** der Strategien ist festes Risiko (`UsePercentRisk = false`) deshalb aussagekräftiger — Profit Factor und Erwartung pro Trade in R bleiben dann sauber vergleichbar. Für die **Simulation der realen Kontoentwicklung** ist das prozentuale Risiko das richtige. Am besten beides laufen lassen: fest zum Bewerten, prozentual zum Hochrechnen.
- **Die anderen vier Strategien nutzen weiterhin festes Risiko** (100 € bzw. berechnete Kontraktzahl). Ein Vergleich des Net Profit zwischen ihnen und dieser Strategie ist damit nicht direkt möglich — entweder hier `UsePercentRisk = false` setzen oder die Prozentlogik dort ebenfalls einbauen.
- **Stichprobengröße:** Die beiden Sperren zusammen kosten spürbar Handelszeit — der Freitag rund 20 % der Tage, das Fenster 09:00–12:00 die volumenstärkste Phase des europäischen Handelstags. Rechne mit deutlich weniger Signalen als vorher und prüfe im Ergebnis zuerst die **Anzahl der Trades**, bevor du Kennzahlen wie Profit Factor interpretierst.
- **Nachrechnung nach dem Fill:** Im Normalfall bleibt der Stop auf dem Candle-Open (absoluter Level), das Ziel wird aus dem tatsächlichen Fill-Abstand berechnet. Im Kappungsfall behält der Stop seinen Geldabstand zum Fill, damit das Risiko exakt 100 € bleibt.
- **Timeframe:** explizit für 1-Minuten-Kerzen. Bei anderer Bar-Größe warnt die Strategie im Log.

### R-Werte durchtesten

`RewardMultiple` ist der Parameter dafür. Im Strategy Analyzer über **Optimize** einen Sweep von z. B. 0,5 bis 4 in 0,5er-Schritten laufen lassen. Zwei Hinweise dazu:

- Der Sweep zeigt dir die **Form** der Kurve über R. Ein sauberes Plateau (mehrere benachbarte Werte funktionieren) ist ein gutes Zeichen, ein einzelner Ausreißer ist Rauschen.
- Den besten Wert aus dem Sweep zu nehmen **ist bereits Optimierung auf diese Daten**. Was dabei herauskommt, gehört auf dem Holdout-Zeitraum gegengeprüft, bevor du ihm glaubst.

---

## Installation in NinjaTrader 8.1

**Variante A — Datei kopieren (empfohlen):**
1. `Strategies/OpeningPullback2R.cs` herunterladen.
2. Nach `Dokumente\NinjaTrader 8\bin\Custom\Strategies\` kopieren.
3. NinjaTrader starten → **New → NinjaScript Editor** → Taste **F5** (Kompilieren).
4. Unten muss „Compile successful" erscheinen.

**Variante B — Copy & Paste:**
1. **New → NinjaScript Editor** → rechts im Ordner **Strategies** Rechtsklick → **New Strategy** → Name exakt `OpeningPullback2R` → im Wizard direkt unten links auf **View Code** klicken (nichts konfigurieren).
2. Kompletten Dateiinhalt aus dem Repo einfügen (alles ersetzen) → **F5**.

---

## Daten: Das musst du VOR dem Backtest klären

NinjaTrader lädt historische Daten **automatisch vom verbundenen Datenanbieter**, sobald der Strategy Analyzer sie braucht. Es gibt aber einen Haken:

**FDXS ist ein Eurex-Produkt — die kostenlosen NinjaTrader-Daten (Free Sim Data) decken nur CME ab.** Für Eurex-Historie brauchst du einen Anbieter mit Eurex-Abo, z. B.:

- **Interactive Brokers** (TWS/Gateway laufen lassen, NT-Connection „Interactive Brokers", Eurex-Marktdaten im IB-Konto abonniert) — liefert gute 1-Min-Historie
- **IQFeed** mit Eurex-Add-on

**Wenn du heute Abend (noch) keinen Eurex-Feed hast:** Mechanik trotzdem testen — kostenlosen NinjaTrader-Account verbinden (Free Sim Data, CME) und die Strategie auf **MES 09-26** oder **MNQ 09-26** laufen lassen mit `OpenHour = 15`, `OpenMinute = 30` (US-Kassaeröffnung, Berliner Zeit). Die Logik ist identisch, nur das Instrument anders. Eurex-Daten dann in Ruhe organisieren.

**Daten prüfen/manuell laden:** **Tools → Historical Data** → links Instrument suchen → Reiter **Download** → Typ `Minute`, Zeitraum wählen → Download. Wenn dort nichts ankommt, liefert dein Anbieter für das Instrument keine Historie.

---

## Backtest im Strategy Analyzer — Schritt für Schritt

1. **Verbinden:** Control Center → **Connections** → deinen Anbieter verbinden (grünes Signal unten links).
2. **New → Strategy Analyzer**.
3. Links im Panel:
   - **Strategy:** `OpeningPullback2R`
   - **Instrument:** `FDXS 09-26` (ggf. über Instruments-Suche hinzufügen)
   - **Data series:** Type `Minute`, Value `1`
   - **Time frame:** z. B. letzte 6–12 Monate. Der SEP26-Kontrakt allein ist erst seit ~Juni 2026 liquide — für mehr Historie in den Properties **Merge policy = MergeBackAdjusted** setzen (hängt die Vorgängerkontrakte 06-26, 03-26, … kursbereinigt an).
   - **Trading hours:** Eurex-Template des Instruments (Standard belassen)
4. **Realistisch machen (wichtig!):**
   - **Commission:** Tools → Commissions → Template anlegen (FDXS grob ~1 € pro Kontrakt/Seite, je nach Broker) und im Analyzer zuweisen
   - **Slippage:** 1 Tick
   - **Order fill resolution:** `High`, darunter `1 Tick` wählen, wenn dein Anbieter Tick-Historie liefert (IQFeed ja, IB nein → dann `Standard` lassen). Bei `Standard` sind Tage, an denen Stop **und** Target in derselben 1-Min-Kerze liegen, geraten — bei 2R-Distanzen selten, aber im Trade-Log stichprobenartig prüfen.
5. **Run** klicken. Ergebnis-Tabs: **Summary** (Kennzahlen), **Trades** (jeder einzelne Trade), **Chart** (Einstiege visuell prüfen!), **Analysis**.

### Worauf schauen

- **Chart-Tab zuerst:** 5–10 Trades manuell nachvollziehen — stimmen Einstieg 9:06+, Stop-Level, 2R-Ziel? Erst wenn die Mechanik sichtbar korrekt ist, sind Kennzahlen relevant.
- Bei 2R-Zielen reicht rechnerisch eine Trefferquote > 33,3 % (vor Kosten). Relevant: **Profit Factor**, **Max Drawdown**, **Anzahl Trades** (unter ~100 Trades ist statistisch wenig belastbar — mit MergeBackAdjusted Historie verlängern).
- Danach: Parameter-Robustheit über **Optimize** + **Walk-Forward** prüfen (kleine Parameteränderung darf das Ergebnis nicht umkippen).

---

## Bekanntes Verhalten / Grenzen

- `Calculate.OnBarClose`: Der Market-Einstieg füllt zum Open der Folgeminute — das entspricht „Einstieg nach Schluss der Signalkerze".
- Feste Kontraktzahl → das €-Risiko schwankt täglich mit der Stop-Distanz (R in Punkten variiert). Positionsgrößen-Normierung auf festes €-Risiko wäre die erste sinnvolle Erweiterung.
- Läuft die Position bis Sessionende ohne Stop/Target, wird sie 30 s vor Schluss geflattet (`IsExitOnSessionCloseStrategy`).
- Mögliche Varianten für den Vergleich später: Zeit-Exit (z. B. flatten 17:30), 1R/3R-Ziele, Break-Even nach 1R.
