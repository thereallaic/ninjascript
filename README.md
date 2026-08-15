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
| `Strategies/TimedLong.cs` | TimedLong | **Benchmark ohne Signal:** täglich um 16:00 long, Ausstieg 22:00. Messlatte für alle übrigen Strategien |

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
4. **Kein EMA-Durchbruch:** Der Open muss bereits auf derselben Seite des EMA50 liegen wie der Close — quert die Kerze die Linie, kein Trade.
5. **Kerzenform:** Ablehnungsdocht darf höchstens **2 ×** so lang sein wie der Körper — sonst kein Trade.
6. **Stop = Open der Signalkerze.**
7. **Positionsgröße** so, dass ein Stopout ungefähr **100 €** kostet.
8. **Ziel = `RewardMultiple` × Stopdistanz** — der frei einstellbare R-Wert, Standard 1.
9. Immer nur **eine Position gleichzeitig**. Offene Position wird um **22:00 glattgestellt**.

> **Sperrzeiten:** Die Vorgabe „keine Trades von 09:00 bis 12:00" ist über den **Fensterstart** abgebildet (`StartHour = 12`), nicht über eine eigene Blackout-Mechanik — bei einem Fenster, das ohnehin um 09:00 beginnen würde, ist beides identisch. Der Freitagsfilter (`TradeFriday = false`) sperrt nur **neue Einstiege**; er steht im Code nach der Glattstellungs-Logik, damit eine offene Position in jedem Fall regulär beendet würde.

### EMA-Durchbruch-Filter

Verworfen wird ein Signal, wenn die Kerze den EMA50 **durchquert** — der Close liegt auf der Handelsseite, der Open lag aber noch jenseits:

```
Long:   Open ≥ EMA50 nötig   (Open < EMA50 → verworfen)
Short:  Open ≤ EMA50 nötig   (Open > EMA50 → verworfen)
```

Referenz ist für Open und Close derselbe EMA-Wert dieser Kerze (`ema[0]`) — also genau das, was man im Chart sieht: Die Linie verläuft durch den Kerzenkörper.

| Signalkerze (Long, EMA50 = 26260) | Open | Close | Ergebnis |
|---|---|---|---|
| komplett über der Linie | 26265 | 26280 | ✅ Trade |
| Open exakt auf der Linie | 26260 | 26275 | ✅ Trade |
| quert die Linie von unten | 26250 | 26275 | ❌ verworfen |

**Warum das sinnvoll ist:** Genau bei diesen Kerzen ist die Richtungsaussage des EMA am schwächsten — der Kurs steht praktisch auf der Linie und kann in beide Richtungen kippen. Dazu kommt, dass der Einstieg zum Close der Bewegung hinterherläuft, die innerhalb der Kerze bereits stattgefunden hat.

**Was du dabei aufgibst:** Das ist zugleich die klassische „Ausbruch durch den gleitenden Durchschnitt"-Kerze — für eine Momentum-Strategie eigentlich das prominenteste Signal. Der Filter entfernt also nicht nur Rauschen, sondern eine ganze Signalklasse. `UseEmaCrossFilter = false` schaltet ihn ab; ein Vergleichslauf mit und ohne zeigt dir, ob diese Kerzen an deinem Instrument tatsächlich schlechter laufen.

### Kerzenform-Filter

Verworfen wird ein Signal, wenn der **Ablehnungsdocht** länger ist als `MaxWickToBodyRatio` mal der Kerzenkörper:

```
Long:   Körper = Close − Open    Docht = High − Close   (oberer Docht)
Short:  Körper = Open − Close    Docht = Close − Low    (unterer Docht)

Docht > 2 × Körper  →  kein Trade
```

Gemeint ist immer der Docht **gegen** die Handelsrichtung — bei Long also oben, bei Short unten. Ein langer Docht dort bedeutet, dass die Gegenseite den Kurs innerhalb der Signalkerze bereits deutlich zurückgedrückt hat: Der Ausbruch ist schon abverkauft, bevor du drin bist.

| Signalkerze (Long) | Körper | Oberer Docht | Verhältnis | Ergebnis |
|---|---|---|---|---|
| Open 26250 → Close 26270, High 26280 | 20 | 10 | 0,5 | ✅ Trade |
| Open 26250 → Close 26270, High 26310 | 20 | 40 | 2,0 | ✅ Trade (Grenzfall, nicht *größer* als 2) |
| Open 26250 → Close 26260, High 26300 | 10 | 40 | 4,0 | ❌ verworfen |

**Warum das hier besonders greift:** Der Körper *ist* in dieser Strategie die Stopdistanz. Die Regel sagt damit übersetzt: Der bereits gelaufene Rückschlag darf höchstens das Doppelte von 1R betragen. Bei `MaxWickToBodyRatio = 2` und 20 Punkten Risiko wird also alles verworfen, was schon 40+ Punkte zurückgekommen ist.

Kleinere Werte filtern strenger (1,0 = Docht darf den Körper nicht überschreiten), `UseCandleShapeFilter = false` schaltet die Prüfung ab. Das Debug-Log gibt bei jedem verworfenen Signal das konkrete Verhältnis aus — damit siehst du, wie viele Signale der Filter kostet.

### Positionsgröße und der Kappungsfall

Hier ist die **Stopdistanz durch die Kerze vorgegeben** und die **Kontraktzahl wird berechnet** — umgekehrt zur Vorgängerversion:

```
Stopdistanz = |Close der Signalkerze − Open der Signalkerze|
Kontrakte   = abrunden( RiskAmount / (Stopdistanz × PointValue) )
```

Abgerundet wird bewusst: Das Risiko liegt damit nie *über* 100 €, bei weiten Stops aber spürbar darunter. Beispiele für FDXS (1 Punkt = 1 €):

| Signalkerze | Stopdistanz | Kontrakte | Risiko | Stop liegt auf |
|---|---|---|---|---|
| Open 26250 → Close 26270 | 20 Pkt | 5 | 100 € | Candle-Open |
| Open 26250 → Close 26256 | 6 Pkt | 16 | 96 € | Candle-Open |
| Open 26100 → Close 26250 | 150 Pkt | 1 *(gekappt)* | 100 € | 100 Pkt unter Entry |

**Kappungsfall:** Ist die Stopdistanz so groß, dass selbst 1 Kontrakt mehr als 100 € riskieren würde, wird 1 Kontrakt gehandelt und der Stop auf genau 100 € **herangezogen**. Er liegt dann nicht mehr auf dem Candle-Open, sondern auf dem Geldlimit — aus einem strukturellen wird ein geldbasierter Stop. Das Debug-Log markiert diese Trades mit `GEKAPPT`, damit du siehst, wie oft es passiert.

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
| `UseCandleShapeFilter` | **true** | Kerzenform-Filter ein/aus |
| `MaxWickToBodyRatio` | **2.0** | Max. Länge des Ablehnungsdochts als Vielfaches des Körpers |
| `UseEmaCrossFilter` | **true** | Kerzen, die den EMA50 durchqueren, erzeugen kein Signal |
| `VolumeLookback` | 20 | Anzahl Kerzen für den Durchschnitt — **ohne** die aktuelle Kerze |
| `EnableLong` / `EnableShort` | true / true | Richtungen einzeln abschaltbar, um sie isoliert zu testen |
| `RiskAmount` | 100 | Geldrisiko je Trade (1R) in Instrumentenwährung (EUR bei FDXS) |
| `RewardMultiple` | **1** | **Der R-Wert zum Durchtesten.** Ziel = Multiple × Stopdistanz |
| `MaxContracts` | 50 | Obergrenze der berechneten Positionsgröße |
| `MinStopTicks` | **10** | Signale mit kleinerer Stopdistanz verwerfen — siehe Kostenrechnung oben. 0 = aus |
| `MaxTradesPerDay` | 0 | 0 = unbegrenzt |
| `EnableDebugLog` | false | Loggt Signal, Volumenverhältnis, EMA, Stopdistanz, Risiko, Kappung, Fill und Zeit-Exit |

### Details, die das Ergebnis beeinflussen

- **Der Volumen-Durchschnitt schließt die Signalkerze aus** (`SMA(Volume, 20)[1]`). Sonst würde eine Volumenspitze ihren eigenen Schwellwert nach oben ziehen und das Signal systematisch abschwächen.
- **„Über dem EMA50" ist als Close > EMA50 umgesetzt** — nicht als „gesamte Kerze inklusive Docht oberhalb".
- **Zeitstempel-Logik:** Die Kerze, die um 12:00 schließt, enthält noch Daten von vor 12:00 und zählt nicht zum Fenster. Erste mögliche Signalkerze schließt um 12:01.
- **Stichprobengröße:** Die beiden Sperren zusammen kosten spürbar Handelszeit — der Freitag rund 20 % der Tage, das Fenster 09:00–12:00 die volumenstärkste Phase des europäischen Handelstags. Rechne mit deutlich weniger Signalen als vorher und prüfe im Ergebnis zuerst die **Anzahl der Trades**, bevor du Kennzahlen wie Profit Factor interpretierst.
- **Nachrechnung nach dem Fill:** Im Normalfall bleibt der Stop auf dem Candle-Open (absoluter Level), das Ziel wird aus dem tatsächlichen Fill-Abstand berechnet. Im Kappungsfall behält der Stop seinen Geldabstand zum Fill, damit das Risiko exakt 100 € bleibt.
- **Timeframe:** explizit für 1-Minuten-Kerzen. Bei anderer Bar-Größe warnt die Strategie im Log.

### R-Werte durchtesten

`RewardMultiple` ist der Parameter dafür. Im Strategy Analyzer über **Optimize** einen Sweep von z. B. 0,5 bis 4 in 0,5er-Schritten laufen lassen. Zwei Hinweise dazu:

- Der Sweep zeigt dir die **Form** der Kurve über R. Ein sauberes Plateau (mehrere benachbarte Werte funktionieren) ist ein gutes Zeichen, ein einzelner Ausreißer ist Rauschen.
- Den besten Wert aus dem Sweep zu nehmen **ist bereits Optimierung auf diese Daten**. Was dabei herauskommt, gehört auf dem Holdout-Zeitraum gegengeprüft, bevor du ihm glaubst.

---

## TimedLong — Benchmark ohne Signal

Kein Setup, keine Bedingung: **jeden Handelstag um 16:00 long, um 22:00 wieder flat.** Über `DirectionLong = false` läuft dieselbe Mechanik short — praktisch, um dieselbe Uhrzeit in beide Richtungen zu prüfen.

Der Zweck ist nicht, damit Geld zu verdienen, sondern eine **Messlatte** zu haben. Jede der vier Signalstrategien behauptet implizit, mehr zu können als „einfach drin sein". Ob das stimmt, siehst du erst im Vergleich gegen diese Baseline. Schlägt eine Strategie sie nicht, misst sie keinen Edge, sondern die Grunddrift des Marktes im gewählten Fenster — bei einem Aktienindex ist die über die Jahre positiv, das ist keine Leistung deiner Regeln.

1. **Einstieg:** erste Kerze, die um 16:00 oder danach schließt → Market-Order, Fill zum Open der Folgekerze (also ≈ 16:00:00).
2. **Ausstieg:** erste Kerze, die um 22:00 oder danach schließt.
3. Genau **ein Trade pro Handelstag**, alle Wochentage einzeln abschaltbar.
4. **Stop/Ziel-Klammer standardmäßig aktiv** (`StopTicks` = 50, `RewardMultiple` = 1), Positionsgröße aus 100 € Risiko.

### Die zwei Modi — und warum die Unterscheidung wichtig ist

**`UseStopTarget = true` (Standard):** Stop bei `StopTicks` Abstand, Ziel bei `RewardMultiple × StopTicks`, Positionsgröße aus `RiskAmount`. Damit läuft die Baseline auf **derselben Risikobasis** wie die übrigen Strategien und ist direkt vergleichbar.

**`UseStopTarget = false`:** Kein Stop, kein Ziel — die Position läuft die vollen Stunden durch. Das misst die **reine Drift** des Zeitfensters und ist der ehrlichere Benchmark, weil keine Wechselwirkung mit Stopdistanz und Volatilität hineinspielt.

> **⚠️ In diesem Modus sind `RiskAmount`, `RewardMultiple` und `UseFixedRisk` wirkungslos** — sie stehen weiter im Parametergitter, tun aber nichts. Ohne Stop gibt es keine Bezugsgröße für ein Geldrisiko, die Positionsgröße ist schlicht `Contracts` (Standard 1), und das Risiko je Trade ist **unbegrenzt**. Die Strategie schreibt beim Start eine entsprechende Warnung ins **Log**-Tab. Vergleichsgrößen sind dann Trefferquote, Erwartung pro Trade und die Form der Equity-Kurve — nicht Net Profit.

### Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `EntryHour` / `EntryMinute` | **16** / 0 | Uhrzeit des täglichen Einstiegs |
| `ExitHour` / `ExitMinute` | 22 / 0 | Uhrzeit des Ausstiegs |
| `TradeMonday` … `TradeFriday` | alle true | Wochentage einzeln abschaltbar |
| `DirectionLong` | **true** | true = Long · false = Short (Stop und Ziel gespiegelt) |
| `UseDailyEmaFilter` | **true** | Übergeordneter Trendfilter auf Tagesbasis ein/aus |
| `DailyEmaPeriod` | **50** | Periode des EMA im Tageschart |
| `DailyEmaAbove` | **true** | true = nur über dem Tages-EMA handeln · false = nur darunter |
| `UseColorFilter` | **true** | Momentum-Filter über die Kerzenfarben ein/aus |
| `ColorLookback` | **20** | Wie viele Kerzen vor dem Einstieg gezählt werden (inkl. der gerade geschlossenen) |
| `MinColorCount` | **10** | Es müssen **strikt mehr** als so viele Kerzen in Handelsrichtung schließen |
| `UseStopTarget` | **true** | true = feste Stop/Ziel-Klammer · false = reine Drift-Messung, Risiko unbegrenzt |
| `StopTicks` | 50 | Stopdistanz in Ticks (FDXS: 1 Tick = 1 Punkt), nur bei aktiver Klammer |
| `RewardMultiple` | 1 | Ziel in R, nur bei aktiver Klammer |
| `UseFixedRisk` / `RiskAmount` / `MaxContracts` | true / 100 / 50 | Positionsgröße aus dem Geldrisiko, nur bei aktiver Klammer |
| `Contracts` | 1 | Feste Größe ohne Klammer bzw. bei `UseFixedRisk = false` |
| `EnableDebugLog` | false | Loggt jeden Ein- und Ausstieg |

### Tages-EMA-Filter (übergeordneter Trend)

Mit `UseDailyEmaFilter = true` (Standard) wird nur gehandelt, wenn der Kurs zur Einstiegszeit auf der verlangten Seite des **EMA50 im Tageschart** liegt:

```
DailyEmaAbove = true   →  Einstieg nur, wenn Kurs ÜBER dem Tages-EMA50
DailyEmaAbove = false  →  Einstieg nur, wenn Kurs UNTER dem Tages-EMA50
```

Dafür lädt die Strategie eine **zusätzliche Tages-Datenserie** (`AddDataSeries(BarsPeriodType.Day, 1)`). Das ist billig — anders als eine Tick-Serie kostet es kaum Rechenzeit und braucht keine Sonderdaten.

> **Kein Look-ahead:** Von Zusatzserien verarbeitet NinjaTrader ausschließlich **abgeschlossene** Bars. Um 14:31 ist die heutige Tageskerze noch nicht fertig und fließt daher nicht in den EMA ein — der Wert stützt sich auf vollständige Vortage. Genau so muss es sein: Würde der heutige Tagesschluss mitzählen, wüsste die Strategie, wie der Tag ausgeht.

**Der Filter wirkt richtungsunabhängig.** Er ist bewusst *nicht* an `DirectionLong` gekoppelt, weil du „über dem EMA" explizit vorgegeben hast. Für einen Short-Lauf im Abwärtstrend setzt du `DailyEmaAbove = false` — dann wird nur unterhalb der Linie geshortet.

### Farbfilter (Momentum-Bestätigung)

Mit `UseColorFilter = true` (Standard) wird nur eingestiegen, wenn die jüngste Kursbewegung zur Handelsrichtung passt:

```
Short: mehr als 10 der letzten 20 Kerzen müssen ROT sein   (Close < Open)
Long:  mehr als 10 der letzten 20 Kerzen müssen GRÜN sein  (Close > Open)
```

Die Schwelle ist **strikt**: Bei `MinColorCount = 10` und `ColorLookback = 20` braucht es mindestens **11** passende Kerzen. Dojis (Close == Open) zählen für keine Seite und wirken damit leicht bremsend.

Gezählt wird auf der geladenen Datenserie — bei 1-Minuten-Kerzen sind 20 Kerzen also die 20 Minuten vor dem Einstieg. Fällt der Filter durch, ist der Tag abgehakt; die Strategie rückt nicht später nach, weil der Einstieg an eine feste Uhrzeit gebunden ist.

Der Filter macht aus dem reinen Benchmark eine bedingte Strategie — für den Vergleichszweck also abschalten (`UseColorFilter = false`), sonst misst du nicht mehr die Grunddrift.

### So nutzt du den Benchmark

1. TimedLong über **denselben Zeitraum, dasselbe Instrument, dieselben Kosten** laufen lassen wie die anderen Strategien.
2. Vergleichsgrößen: **Erwartung pro Trade**, Profit Factor, Max Drawdown — nicht Net Profit (unterschiedliche Positionsgrößen).
3. Die Einstiegszeit ist ein Parameter: Ein Sweep über `EntryHour` zeigt dir, ob es am FDXS überhaupt Tageszeiten mit systematischer Drift gibt. Das ist als Diagnose nützlich — aber die beste Stunde aus so einem Sweep zu übernehmen wäre wieder Optimierung auf die Testdaten.

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
