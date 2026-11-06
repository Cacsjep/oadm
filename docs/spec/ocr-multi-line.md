# Text recognition: rows

Status: **spec, not built** (2026-11-06). The numbers below come from a prototype on the real models, on the CPU; nothing of
it is in the repository. Open points: "Before release".

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Extends [ocr.md](ocr.md) "Several lines". When this is built, its rules move there and this file ends.

An area's text boxes become rows, so what belongs on one line stays on one line, at any turn of the text: an article and
its price on a receipt, a label and its value on a sign. It needs no model: only arithmetic on the boxes the recognition
already has.

## Rules that never break

1. **Always on** (decision 2026-11-06). No setting per reader or area: it costs nothing to run, and the old order is only
   wrong on turned text, so nobody would choose it.
2. **No new event value** (decision 2026-11-06). `Text read` carries the rows with line breaks. `Text` stays on one line.
3. **`Text` never holds a line break.** Rule checks (`CompiledChecks`: `equals`, `contains`, `starts with`), the change
   check of a watching reader, the vote and `Number` all work on it. A line break there would break an `equals` on a text
   of two rows, and a row that splits in one frame would count as a new value and fire an event.
4. **A value never fakes a log line.** The event log and the server log stay on one line per entry, whatever an action
   logs: the host sees to it, not each action ("Outputs").
5. **A value never adds a line at a TCP receiver**, where a line is often a command ("Outputs").
6. **Measured accuracy** ([ocr.md](ocr.md) rule 6): the recorded test set keeps or raises its score.

## Why

- `LineOrder` works out rows from the level box (`TextLine.Box`) and drops them after sorting: the result is a flat list.
- On turned text the level boxes overlap across rows, so the order is mixed. A receipt turned by 180 degrees gives
  `Thank you! | ORDER 23F7H | 10.60 | TOTAL EUR | 1.90 | 1x Water | ...`.
- Every line already knows how it lies: `TextLine.Turn` (`LineTurn`) holds the centre, the size along and across the text
  and the direction of the text, its 90 degree and upside down turns included.
- `TextMatch.RawText` already holds the lines with line breaks. `TextReadEvents.Data` flattens them just before the event.

## Rows

In `Matching/LineOrder`, used by the matcher and by `OnnxTextRecognizer.Order`. All in pixels of the picture, on `LineTurn`.
A line without `Turn` uses its level box, as today.

1. **Blocks by direction.** Longest line first: it sets a direction, and every line within 15 degrees of it, either way round,
   joins its block. A short line (shorter than 1.5 times its height) has no reliable direction and joins the first block.
   The next line left over starts the next block, so two receipts at different angles stay apart.
2. **The block's axis.** The length-weighted mean of the directions with the angles doubled, because a line has no front.
   The side comes from which way most of the text length reads, so a line of digits read the wrong way round does not turn
   the block.
3. **Rows.** Two lines share a row when their centres lie, across the axis, within 0.55 of the smaller line's height. Rows are
   the connected groups of that relation (at most 30 lines, so at most 435 pairs).
4. **Order.** Rows by their mean place across the axis, the lines of a row along it.
5. **Duplicates.** Two lines of a row that overlap along the axis by more than half of the shorter one are one text (the tile
   overlap gives `5.80  5.80` or `5.80  .80`): the narrower line goes. `BoxMerger` misses these today.

One axis per block, never a line's own direction: an error of 2 degrees is 17 px at 500 px, more than half a line height, so a
wide table would fall apart (measured below).

There is no limit on the gap inside a row. A table with its prices far right is what this is for, and two columns cannot be told
from two objects by geometry.

## Values

| Value | Holds | Used by |
|---|---|---|
| `Text` | one line, joined with a space as today (a line ending in "-" without one), in the new order | rule checks, change check, vote, `Number` |
| `Text read` | one row per line, the lines of a row joined with a space, at most 500 characters | messages |

- With a format `Text` is the match and `Text read` the whole area, in rows.
- `TextReadEvents.Data` stops flattening `Text read`. `TextReadEvents.Join` itself stays: `Number` and the log line of the
  action Read text need it.
- `AreaResult.TextRead` is the source. A voted value takes it from the reading that stands for the vote
  (`FrameVote.Current`), so where the vote changed a character the two can differ by that character. Accepted.
- The word order in `Text` and `Text read` changes on turned or tabular text. A rule on one word is unaffected; a rule on a
  phrase that only matched the old mixed order stops matching. The changelog says so.
- A rule on `Text read` with `equals`, or with `contains` across rows, no longer matches. `contains` inside a row does.
- A rule that already sends `{textRead}` sends line breaks where it sent spaces: MQTT text, HTTP plain, the email text,
  Modbus and OPC UA text. With a format no value holds the whole area on one line any more (rule 2). Accepted; the
  changelog says so.

## Page

- The results table shows rows as lines. Today a row without a format holds `Text`: it takes the text with line breaks
  instead (`RecentRead`, `OcrRows`), at most 500 characters. The cell already wraps.
- The boxes on the video do not change: one box per line found.

## Outputs

Checked in the code, 2026-11-06. The value is `Text read` with line breaks.

| Output | Today | Change |
|---|---|---|
| Event log, event entries | Only "Happened", "On", "Off" | None |
| Event log, action Read text | `ctx.Log` through `TextReadEvents.Join`, one line | None: it must keep using `Join` |
| Event log, other actions | Addresses and counts, never the value; a TCP answer without control characters | None |
| **Event log and server log, any action** | `ctx.Log` and an action's failure text are written as they are (`ActionRunner`): a plugin's action that logs a value adds a line | The host makes both one line, see below |
| Log entry action | `ReplaceLineEndings(" ")`: a value never fakes a line | None |
| Email | Subject one line, Text body keeps the rows, HTML body shows one line until `<pre>` | Docs |
| HTTP | Headers: a break becomes a space. URL and form encoded. JSON and XML escaped. Plain body keeps the rows | None |
| MQTT payload | JSON escaped, plain keeps the rows | None |
| **MQTT topic** | Nothing refuses a line break | `FitsInTopic` refuses control characters: "cannot go in a topic" |
| **Send TCP message** | Text and XML delete line breaks: `10.60ORDER 23F7H` | `PlaceholderEscape.SingleLine`, see below |
| VAPIX command | Path, query and form encoded (`%0A`), JSON escaped, a header refused ("must not contain line breaks", nothing sent); a text or XML body takes the break as it is. The field's pattern and length apply | None, the text body: "Before release" |
| Modbus text | The break goes in as LF, a value over the set length is refused | Docs |
| OPC UA text | Written as it is | Docs |
| Dashboard table | One line: `TableText` turns a break into a space, cut at 100 characters | None |
| Dashboard value widget | Not verified: may look cramped | Headless screenshot |

**`SingleLine`** (`ActionPlaceholders.Escape`) turns a line break into one space (CR LF is one break) and still removes
every other control character. A value still never adds a line at the receiver. A change of the SDK: update
`docs/spec/event-rules.md`, `website/src/content/docs/dev/event-rules.md` and `ActionPlaceholderTests`.

**One line in the logs** (`ActionRunner`): the host turns line breaks into a space in every `ctx.Log` line and in an
action's failure text, before the event log and the server log get them. Rule 4 then holds for actions of other plugins
too; the bundled actions already keep to it by themselves.

## Cost

Measured on 24 cores, an unoptimised prototype with LINQ:

| Step | Time |
|---|---|
| Rows, 30 lines (the most a reading has) | about 13 µs, 31 KB per area |
| `LineOrder.Order` today, 30 lines | about 4.5 µs |
| A small area read ([ocr.md](ocr.md) "Limits") | about 28 ms |
| A whole picture on the CPU | about 630 ms (median) |

So about 0.05 % of a small area's reading. No pixels, no model, no graphics card. Unchanged areas run no model and stay cheap.

## Measured

Receipts of the accuracy set's generator, Search over the whole full HD picture, one axis with duplicates removed. "Both read"
is what the recognition delivered: grouping can reach no more.

| Test | Both read | In the right row |
|---|---|---|
| Every 15 degrees, scale 0.8 | 87/96 | 87/96 |
| Every 15 degrees, scale 1.3 | 84/96 | 84/96 |
| Up to 10 degrees from level or upside down | 40/40 | 40/40 |
| Wide table, price 25 characters away | 32/32 | 32/32 |
| Mild tilt to the side | 16/16 | 16/16 |
| Strong tilt from the top | 16/16 | 16/16 |
| Strong tilt to the side | 16/16 | 11/16 |
| Tilt both ways, turned 30 degrees | 8/8 | 5/8 |
| Damaged receipts of the accuracy set | 49/80 | 49/80 |

- Today's order for comparison: the price came straight after its article 18 times of 192, on the same receipts with a dashed
  line in them.
- Without duplicate removal the 1.3 row reads 72/96.
- The order of the rows was right on all 48 flat pictures, 14 of 20 of the damaged ones.
- The direction of each line taken alone (a local axis) gives 16/16 and 8/8 on the two tilted rows that miss above, but 29/32 on the
  wide table, 14/16 from the top and 83/96: rejected.
- Regression: the 22 any-text values of the accuracy set read the same as before, 20 exact.

## Limits

- **Two objects side by side at the same angle**: their rows mix. One area per object. Different angles stay apart.
- **Strong tilt to the side**: a row can split in two lines. It stays readable, nothing is mixed.
- **Blocks under strong tilt**: the fan of directions splits the text into two blocks, which can swap (3 of 4 pictures).
- **What the recognition loses stays lost**: two close lines merge at about 45 to 60 degrees ([ocr.md](ocr.md), "Why it
  breaks"). Rows do not repair that.

## Before release

- Real pictures from the cameras: all numbers above are generated receipts.
- The accuracy test with a format: the new order feeds the matching of neighbouring lines, and only any-text values were compared.
- The dashboard value widget.
- The text body of a VAPIX command: accept the line break (the command's author sets the field's pattern), or fill it
  with `SingleLine` like a TCP message. Not decided.
- Whether a mean of the axis per neighbourhood gets the tilted rows without losing the wide table (not measured).

## Tests

- `LineOrder`, from recorded `LineTurn` values: rows at every 15 degrees, upside down, a wide table, two blocks at different
  angles, a line without `Turn`, duplicates, a digit line read the wrong way round.
- Real models: a receipt at 0, 90, 180 and 270 degrees and turned 15 degrees gives its rows in `Text read`.
- `Text` holds no line break; `Text read` does; `Number` is unchanged.
- A row that splits in one frame fires no event.
- The Read text log line is one line.
- `SingleLine`: a break becomes a space, CR LF one space, other control characters go, no second line at a TCP receiver.
- The host: an action that logs a value with a line break, or fails with one in its text, gives one line in the event log
  and in the server log.
- MQTT: a line break in a topic value is refused.
- `Perf`: 30 lines per area at 15 frames per second, with the measured time in the message.
- The accuracy set stays at its baseline.
