---
title: UI kit
description: The shared controls, theme, validation and icons for plugin dialogs and pages.
---

Plugin dialogs and pages are Avalonia views with view models (CommunityToolkit.Mvvm). They use the host's controls and
theme, so a plugin looks and behaves like the rest of OADM in dark and light. Two rules cover most of it:

- **Reuse, never copy.** Every element that exists in OADM is a shared control. Use it.
- **No local styles.** No colors, font sizes, weights or paddings in your views. Use the theme's classes and
  `Oadm.*Brush` keys (`StaticResource`, or `TryFindResource` in code), never a color value: both appearances define
  every key.

Add the namespace `xmlns:ui="using:Oadm.Sdk.Client.Controls"` to your XAML.

## Shared controls

### Dialogs and forms

| Control | Use for |
|---|---|
| `ui:DialogTitleBar Text` | The first row of every dialog: the title as a heading and an X that closes it |
| `ui:DialogFooter CancelCommand` | The last row: Cancel on the left, your buttons on the right (`Button.secondary` for Back, one `Button.primary`); a lone Close or Cancel sits on the right |
| `ui:CardHeader Title Description` | The heading of a card; child controls go right of the title (a device picker). Class `flush` when the content below is optional and brings its own top margin (`Oadm.GapTop`) |
| `ui:FormField Label Hint Error` | **Every labeled input**: the label left, level with the input; below the input its validation error, else the `Hint`. The widths come from the theme (`Oadm.FormLabelWidth` 180, `Oadm.FormInputWidth` 280; a narrower row shrinks the input). Class `wide`: the input fills the row. Class `inline`: the label sizes to its text (fields in one line, From / To). `LabelAbove` (SDK 0.5.0): the label above a full-width input. Without a label the label column stays empty, so a check box or a button row lines up with the inputs. `Error` only for content without data validation (a group of check boxes, a text value) |
| `ui:FormField Info`, `ui:InfoIcon Text` | The explanation of a field as an info icon right of the input (hover and click), instead of a long hint. `InfoPicture` / `PictureTemplate` (a `DataTemplate`) adds a drawing below the text |
| `ui:PasswordBox Text` | **Every password field**: a bullet mask and an eye button ("Show password" / "Hide password"). Never a `TextBox` with `PasswordChar` |
| `ui:FileRow FileName Details Error Command` | A chosen local file with its "Choose file" button; write sizes with `ByteSize.Text` |
| `ui:WizardSteps ItemsSource` | The step row of a wizard (`WizardStepItem`: index, name, current step, can open, open) |
| `ComboBox Classes="grouped"` + `GroupedChoice.Build(items, group, text)` | A select field with grey group headers and indented choices; bind `SelectedItem` to a `GroupedChoice`, its `Value` is the item |
| `ComboBox Classes="multiSelect"` + `CheckChoice` / `CheckChoiceList` | A select field with check boxes (`ICheckChoice` items) that stays open while the user ticks; `CheckChoiceList` offers "All" or chosen items: `<ComboBox Classes="multiSelect" ItemsSource="{Binding Sites.Entries}" PlaceholderText="{Binding Sites.Summary}" />` |
| `ui:CodeView Text Language ContentType IsFormatted` | **Every read-only code or answer** (bodies, headers, JSON and XML previews): monospace, selectable, no wrapping. See [Code](#code) |
| `ui:CodeEditor Text Language PlaceholderText` | **Every editable code field** (request bodies): a monospace text box colored like `ui:CodeView` while typing; the text stays exactly as typed |
| `MessageWindow.ConfirmAsync(owner, title, message, confirmText)`, `ShowMessageAsync` | **Every message and confirmation**; the host uses the same window. A dialog confirms a risky change on Apply this way, never with an inline warning and a check box |
| `DevicePickerWindow.ShowAsync(owner, devices, selected, unavailableReason)` | **Every "choose devices" dialog**: search, tags, Select all, the count, 100,000 devices. Returns the chosen ids, or null for Cancel; `unavailableReason` greys devices with its text |
| `DevicePickerWindow.ShowOneAsync(...)` | The picker for one device (checking one unchecks the other); returns its id or null |
| `DevicePickerWindow.ShowChoiceAsync(owner, devices, initial, DevicePickerOptions.Full(eventDeviceText))` | The picker with All devices, tags and, when you pass its text, the device of the event. Returns a `DeviceChoice`: All, EventDevice, or Chosen with `DeviceIds` and `Tags` (a tag means every device with it) |
| `CertificateWindow.ShowAsync(certificate, owner)` | The details of one certificate (`CertificateDetails`): validity, issued to and by, key and signature, fingerprint, uses, names; every value can be copied |

### Pages, toolbars and lists

| Control | Use for |
|---|---|
| `ui:PageHeader` | `Subtitle`, `Trailing` and `IsHidden` of the host page header, set on your page view, see [Layouts](#layouts) |
| `ui:ToolbarButton Text IconKey (or Icon) IsPrimary IsIconOnly` | Every toolbar button: flat, an icon and text. `IsPrimary` marks the main one of a toolbar (never filled); `IsIconOnly` shows only the icon, with `Text` as the tooltip |
| `ui:ToolbarSeparator` | The vertical line between toolbar groups |
| `ui:ToolbarOverflowPanel Spacing` | Every row of toolbar buttons on a page: what does not fit on a small or scaled screen goes behind a More button. Keep the device choice button and the search box outside it |
| `PageDeviceChoice` | The device choice of a page that reads many devices: a toolbar button with the devices icon ("Choose devices", "Selected devices: ..."), see [Core plugins](../core-plugins/#choose-devices-first) |
| `ui:SearchBox Text` | Every search field. Class `stretch` fills a narrow card |
| `ui:IconLabel Icon Text`, `ui:OadmIcon Data` | An icon with text (button content), a single icon |
| `ui:StatusChip Text` | Every status: a colored icon (`IsOk`, `IsWarning`, `IsError`, `IsAccent`, `IsInfo`) and plain text; in a grid cell with `Margin="10,0"`. `MaxLines` 2 in a grid with `wrapRows` |
| `ui:StatCard Title Value Detail` | A count card of a monitoring page: a large count and a detail line. Set `IsOk`, `IsWarning` or `IsError` only while the count needs attention |
| `ui:ProgressRow Value Text IsActive` | An upload or a scan: a progress bar (0 to 100) with its status text |
| `ui:ProgressRing` | A spinning ring in place while a list or card loads |
| `ui:LoadingOverlay Text` | A dialog or page that waits for a read: a ring and text in the middle. Bind `IsVisible`; keep the footer free, so Cancel stays usable |
| `ui:OffState Icon Title Text` | A page or tab whose feature is off: a large icon, the title ("Site nodes are off"), one or two sentences and your controls below (the Turn on button, the line for users who may not), centered |
| `ui:DeviceIcon Category Status ShowStatusToolTip` | Every device icon in a list: the category's icon, amber or red by status, the status as the tooltip |
| `ui:DeviceTree` + `DeviceTreeModel` | Devices grouped by tag, with search, an optional `Filter` and `DeviceInvoked` (Web View, Live View, Dashboards) |
| `ui:PageTab Title IsActive ActivateCommand CloseCommand Count` | Every tab of a page with tabs: the active one underlined. `Count` above 0 shows a badge; without `CloseCommand` the tab is fixed (no X) |
| `ui:TileGrid Source` | A grid of tiles that fills the page (Live View, Dashboards), see [Tiles](#tiles) |
| `ui:LineChart`, `ui:BarChart` | A line chart over time (`ChartSeries`, a threshold line, named `ChartBand`s) and a bar chart (`ChartBar`), in the theme's colors (`ChartColor`). Used by the Load test |
| `RowActions.Show(anchor, actions)` | The ⋯ menu of a table row (`Button.toolbar.iconOnly` with `Icon.more`, tooltip `RowActions.Tip`): everyday entries, a separator, the red ones last |
| `ui:IssuedCertificateList Toolbar Banner` | A paged list of certificates (the PKI page). Its data context is an `IssuedCertificateListViewModel` over your `IIssuedCertificateSource`: filters, search, pages of 500, several rows chosen, View certificate and one ⋯ menu per row (`MenuOf`, the same as the right click). Set `WorkText` while your own write runs: the progress row shows it |
| `GridGroup(Name, IconKey)` | The group key of a grouped grid (`DataGrid.groups`) whose header shows an icon before the name; a plain string key shows the name only |
| `GridSort.IsMovingSelection(grid)` | True while a sort takes a large selection off a grid and puts it back. A `SelectionChanged` handler that hands `SelectedItems` to the view model returns early then: the same rows are selected after |

For lists that a grid shows, use `RangeObservableCollection<T>` (`Oadm.Sdk.Client.Collections`): `ReplaceAll`,
`AddRange`, `InsertRange`, `RemoveAll` and `RemoveFirst` raise one change for the whole batch, never one per item.

`DeviceTexts.Removed(id)` names a device OADM no longer manages. `ServerErrors.Message(ex)` reads the user text of a
failed server call without a gRPC reference in your project.

### Code

`ui:CodeView` shows text in the `Language` you set: `Auto` (default: from `ContentType`, then from the text), `Json`,
`Xml` (also SOAP), `KeyValue` (param.cgi `key=value`, `# Error` lines red) or `Plain`.

- JSON and XML are pretty-printed with 2 spaces, also when they arrive minified. `IsFormatted="False"` shows the exact
  text, still colored: use it for a Pretty / Raw switch.
- Text that does not parse, or is longer than `HighlightLimit` (512 K characters), shows without colors.
- The colors are theme brushes (`Oadm.Code.KeyBrush`, `StringBrush`, `NumberBrush`, `LiteralBrush`, `PunctuationBrush`,
  `TagBrush`, `AttributeBrush`, `CommentBrush`, `ErrorBrush`).
- `CodeText` (`Detect`, `Prepare`, `FormatJson`, `FormatXml`, `Tokenize`) is the same logic without UI.

### Tiles

`ui:TileGrid` is the grid of Live View and Dashboards. Your tab implements `ITileGridSource`:

- Its tiles are `ITileGridItem`: `Rect` (a `TileRect` of cells), the title, the content or a message, the remove
  command, `IsSelected` (the tile whose settings the page shows; its frame is marked), `FrameClass` (a theme class of
  the frame, e.g. `valueTile`: no video background) and `CopyCommand` (a copy icon left of the X, shown while it can
  run; its tooltip is `ITileGridSource.CopyText`).
- `IsEditing` turns moving and sizing on; the grid asks `TryMove(item, rect)` for every change.
- The grid draws a `TileFrame` per tile. `ShowPreview` shows where a dragged item lands, `ShowSelectionArea` a selection
  rectangle.
- The rules are an `Oadm.Sdk.Layout.TileLayout`: the grid's size, the smallest tile, the drop size and the most tiles,
  with `Place`, `PlaceSized`, `Fit`, `Drag(rect, TileEdges, dx, dy)`, `Fits` and `FirstOverlap`. It has no UI: the
  server part checks a layout with it again before it stores one.

## Theme classes

The styles come from the host theme. The classes you need most:

| Class | Use for |
|---|---|
| `Border.card`, `Border.dialogBody` | A card; the padded body of a dialog with one form |
| `StackPanel.form` | A stack of `ui:FormField`s, with the row spacing |
| `StackPanel.inputRow` | Several inputs in one line, top-aligned, so an error below one input moves nothing else |
| `StackPanel.optionDetail` | The inputs of a radio button choice, indented below it |
| `TextBlock.secondary`, `TextBlock.fieldLabel`, `TextBlock.warning`, `TextBlock.error` | Text roles |
| `Button.primary`, `Button.secondary`, `Button.toolbar`, `Button.danger`, `Button.toolbar.danger`, `Button.link.danger` | See [Buttons](#buttons) |
| `Border.vseparator` | A vertical line |
| `DataGrid.wrapRows` | Rows grow and status chips show two lines |
| `DataGrid.highlightRows` + `DataGridRow.highlight` | An accent tint that fades in and out, e.g. on the rows a link pointed to |
| `CellStyleClasses="number"` + `TextBlock.numberHeader` | A right-aligned numeric column and its header |
| `Border.tile` | A picture tile; its check box is the selection |
| `Border.liveViewSurface` | A dark picture surface |
| `Button.picture` | A clickable picture without button chrome |

## Layouts

**A dialog with one form** has no card: title bar, the form in `Border.dialogBody`, footer. Fixed width,
`SizeToContent="Height"`:

```xml
<Window ... Width="500" SizeToContent="Height" CanResize="False"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False" WindowDecorations="BorderOnly">
  <Grid RowDefinitions="Auto,Auto,Auto">
    <ui:DialogTitleBar Grid.Row="0" Text="Set UPnP name" />
    <Border Grid.Row="1" Classes="dialogBody">
      <StackPanel Classes="form">
        <ui:FormField Label="Name">
          <TextBox Text="{Binding Name}" />
        </ui:FormField>
      </StackPanel>
    </Border>
    <ui:DialogFooter Grid.Row="2" CancelCommand="{Binding CancelCommand}">
      <Button Classes="primary" Content="OK" Command="{Binding ApplyCommand}"
              ToolTip.Tip="{Binding FormError}" ToolTip.ShowOnDisabled="True" />
    </ui:DialogFooter>
  </Grid>
</Window>
```

**A dialog with several sections or a list** uses cards (`Border.card`, `Margin="16,0"`, `16,16,16,0` for each further
card) between title bar and footer, with a fixed width and height (for example 1040 x 700). Every dialog is modal, has
no system title bar (`WindowDecorations="BorderOnly"`) and a fixed size (`CanResize="False"`).

**A page** starts with its content. The description goes into the host's page header, never into a card heading that
repeats the page title. Set it with attached properties on the page's root control:

```xml
<UserControl ... ui:PageHeader.Subtitle="Answers the time requests of the cameras.">
  <ui:PageHeader.Trailing>
    <!-- optional: a value the whole page describes -->
  </ui:PageHeader.Trailing>
  <!-- the card content starts directly with the form -->
</UserControl>
```

The host shows the subtitle next to the page title and the trailing control right of it; the trailing control keeps the
page's data context. A status sits directly left of the Save button it reports on, never top right in the header.

## Validation

Every validation error appears **directly below its input**: small red text aligned with the input's left edge and a
red border, no space reserved without an error. Never an error list at the bottom of a dialog or a summary line under a
card. Form view models derive from `ValidatingViewModel` (`Oadm.Sdk.Client.Validation`: an `ObservableObject` with
`INotifyDataErrorInfo`) and register one rule per property:

```csharp
public sealed partial class UserViewModel : ValidatingViewModel
{
    public UserViewModel()
    {
        Validation
            .Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(Confirm), () => Confirm != Password ? "The passwords do not match." : null);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    // ...
}
```

The theme shows the error of the bound property below a `TextBox`, `ui:PasswordBox`, `NumericUpDown` or `ComboBox`;
`ui:FormField` hides its hint meanwhile.

- An error shows once the field was edited, the user left it (`ui:FormField` tells the view model on lost focus, through
  `IFormFieldVisits.FieldLeft`; an empty required field says so) or the user tried to submit; never on an untouched
  form.
- The rules always run. Submit buttons stay disabled while any error exists (`IsFormValid`, or the command's
  `CanExecute` with `Validation.IsValid`), with the reason as the tooltip (`FormError`, with
  `ToolTip.ShowOnDisabled="True"`). Override `OnValidationChanged` to refresh commands.
- An error of a whole table ("Not enough addresses") goes directly below the table; an error of one row stays in its
  status column.
- Rows that are their own view models (command fields in a list) derive from `ValidatingViewModel` too; their errors
  show below their inputs in the row template.

`Validation` is a `FormValidator`:

| Member | Use |
|---|---|
| `Rule(property, () => message or null)` | One rule for one property |
| `Rules(properties, () => dictionary)` | One check for several properties, e.g. the validator your server part uses for the payload |
| `ShowAll(...)` | The user tried to submit: show every error |
| `Reset(...)` | After loading or prefilling values: back to untouched |
| `SetServerError(property, message)` | An answer of the server or the device that belongs to a field: shown at once, cleared when the field is edited |
| `IsValidFor(...)`, `FirstErrorOf(...)` | A part of a form, e.g. an inline editor |

A view model that cannot change its base class owns a `FormValidator` and forwards `INotifyDataErrorInfo` to it.

## Buttons

- **Command buttons** in toolbars and card headers are flat: `ui:ToolbarButton` (or `Button.toolbar`), icon and text, the
  icon in the accent colour, never filled. Actions left, search right.
- **Icon-only buttons** (close, ⋯, copy, row actions) use the text colour, the accent only on hover, and always have a
  tooltip.
- **One accent button per view** (`Button.primary`, filled): the one main action of a dialog or form (Save, Create, OK).
  Its other actions are `Button.secondary`. The device choice of a page is a command button in its toolbar, a
  `Button.secondary` in a form or dialog.
- **Destructive actions are red**: `Button.danger` (filled) only for the confirming button of a dialog that deletes or
  removes, `Button.toolbar.danger` for flat buttons everywhere else, `Button.link.danger` for row links. `MessageWindow` turns its confirm button red when the text starts with Delete, Remove or Release.
  Destructive entries sit apart from everyday ones (a separator, last in a menu).
- Button and menu labels never end with "...", also when they open a dialog or a file picker.

## Grids

- Tables are `DataGrid`s with `RangeObservableCollection` sources: virtualized, one change per batch.
- Statuses are `ui:StatusChip` in a template column. Grids whose status explains a problem get `Classes="wrapRows"` so
  the text can take two lines.
- Numbers are right-aligned: `CellStyleClasses="number"` and a header `TextBlock.numberHeader`.
- An empty list says in one line what to do first.

## Icons

Icons are resources of the client, available to plugin windows at runtime. A plugin project cannot resolve them at
compile time, so use `DynamicResource` in XAML (`Icon="{DynamicResource Icon.refresh}"`). An `IconKey` property (a
task's menu icon, a toolbar button) takes the key without `Icon.` (`IconKey="refresh"`). The keys:

`activity`, `add`, `app`, `arrowUpRight`, `audit`, `barChart`, `bell`, `braces`, `calendarClock`, `cancel`, `check`, `chevronDown`, `chevronLeft`, `chevronRight`,
`chevronUp`, `circle`, `clipboardCheck`, `clock`, `close`, `cloud`, `cloudCheck`, `columns`, `command`, `compare`, `copy`,
`crossing`, `deleteAll`, `details`, `devices`, `download`, `edit`, `eraser`, `export`, `externalLink`, `eye`, `eyeOff`,
`file`, `filter`, `firmware`, `flame`, `flow`, `focus`, `focusArea`, `folder`, `gauge`, `globe`, `grid`, `gridAdd`,
`groupBy`, `hardDrive`, `hardDriveDownload`, `hash`, `help`, `hold`, `home`, `hourglass`, `identify`, `info`, `key`, `layers`, `lightbulb`,
`lock`, `lockOpen`, `log`, `logout`, `logs`, `maximize`, `merge`, `metronome`, `minimize`, `more`, `network`, `panelClose`,
`panelOpen`, `pause`, `pin`, `plugin`, `polygon`, `range`, `redo`, `refresh`, `remove`, `restart`, `route`, `save`, `scan`,
`search`, `send`, `server`, `settings`, `shield`, `shieldAlert`, `sliders`, `snapshot`, `squareA`, `squareActivity`,
`squareB`, `squareFunction`, `start`, `stop`, `tag`, `tasks`, `text`, `thermometer`, `toggle`, `undo`, `upload`, `user`,
`users`, `video`, `workflow`, `wrench`, `zap`.

Device categories: `device.camera`, `device.encoder`, `device.speaker`, `device.audio`, `device.intercom`,
`device.radar`, `device.io`, `device.door`, `device.generic`.

Statuses: `status.ok`, `status.warning`, `status.error`, `status.info`, `status.neutral`, `status.running`.

In a checkout of OADM a new icon goes into `src/Oadm.Theme/Icons.axaml` (a 24 x 24 Lucide stroke geometry) and into
this list.

## Wording

Texts are for installers who know Axis devices. Short and plain:

- Intros and subtitles: one short sentence, or none. Never explain what a visible control already shows.
- No protocol, API or internal names in UI text. Say what the user can do.
- Tooltips only when they add something, at most about eight words.
- Step names are imperatives with the article: "Wait for the device to come back".
- Errors say what happened and, when nothing was changed, "Nothing was changed".
- No filler: no "simply", "easily", "please note".

## Reference

Every member with its signature in the [SDK reference](../../api/): [`Oadm.Sdk.Client.Controls`](../../api/sdk/client/controls/), [`ValidatingViewModel`](../../api/sdk/client/validation/validatingviewmodel/), [`FormValidator`](../../api/sdk/client/validation/formvalidator/), [`RangeObservableCollection<T>`](../../api/sdk/client/collections/rangeobservablecollection-1/), [`MessageWindow`](../../api/sdk/client/controls/messagewindow/), [`DevicePickerWindow`](../../api/sdk/client/controls/devicepickerwindow/).
