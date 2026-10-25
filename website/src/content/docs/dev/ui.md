---
title: UI kit
description: The shared controls, theme, validation and icons for plugin dialogs and pages.
---

Plugin dialogs and pages are Avalonia views with view models (CommunityToolkit.Mvvm). They use the host's controls and
theme, so a plugin looks and behaves like the rest of OADM in dark and light. Two rules cover most of it:

- **Reuse, never copy.** Every element that exists in OADM is a shared control. Use it.
- **No local styles.** No colors, font sizes, weights or paddings in your views. Use the theme's classes and
  `Oadm.*Brush` keys; both appearances define every key.

Add the namespace `xmlns:ui="using:Oadm.Sdk.Client.Controls"` to your XAML.

## Shared controls

| Control | Use for |
|---|---|
| `ui:DialogTitleBar` | The first row of every dialog: the title and an X that closes it |
| `ui:DialogFooter` | The last row: Cancel on the left, your buttons on the right |
| `ui:CardHeader` | The heading of a card, with optional controls right of the title |
| `ui:FormField` | Every labeled input: label, input, and the error or hint below it. Classes `wide`, `inline`; `Info` adds an info icon |
| `ui:PasswordBox` | Every password field, with the eye button |
| `ui:SearchBox` | Every search field |
| `ui:ToolbarButton`, `ui:ToolbarSeparator` | Toolbar buttons (icon and text) and the line between groups |
| `ui:ToolbarOverflowPanel` | A row of toolbar buttons: what does not fit on a small or scaled screen goes behind a More button |
| `ui:IconLabel`, `ui:OadmIcon` | An icon with text (button content), a single icon |
| `ui:StatusChip` | Every status: a colored icon (`IsOk`, `IsWarning`, `IsError`, `IsAccent`, `IsInfo`) and plain text |
| `ui:StatCard` | A count card of a monitoring page |
| `ui:ProgressRow` | A progress bar with its status text |
| `ui:LoadingOverlay` | A dialog that waits for a read: a ring and text in the middle, Cancel stays usable |
| `ui:FileRow` | A chosen file with its "Choose file" button |
| `ui:CodeView`, `ui:CodeEditor` | Read-only code (JSON, XML, param.cgi answers, colored) and an editable code field |
| `ui:InfoIcon` | An explanation that shows on hover and click |
| `ui:WizardSteps` | The step row of a wizard |
| `ui:DeviceIcon`, `ui:DeviceTree` | A device's icon by category and status, devices grouped by tag |
| `ui:PageTab` | Every tab of a page with tabs: title, X, the active one underlined. `Count` above 0 shows a badge; without `CloseCommand` the tab is fixed (no X) |
| `ui:TileGrid` | A grid of tiles that fills the page (Live View, Dashboards): your tab implements `ITileGridSource`, its tiles `ITileGridItem` (`FrameClass`: a theme class of the frame, `CopyCommand`: a copy icon left of the X); the rules are an `Oadm.Sdk.Layout.TileLayout` (`TileRect`, `Place`, `PlaceSized`, `Fit`, `Drag`) the server checks again |
| `ui:ProgressRing` | A spinning ring in place while a list or card loads |
| `ui:OffState` | A page or tab whose feature is off: icon, title, one or two sentences, and the button that turns it on, centered |
| `ui:RowActions` | The ⋯ menu of a table row: everyday entries, a separator, the red ones last |
| `ui:IssuedCertificateList` | A paged list of certificates with groups, filters, search and row menus (the PKI page) |
| `CertificateWindow` | The details of one certificate: validity, issued to and by, fingerprint, uses (`ShowAsync`) |
| `ui:PageHeader` | `Subtitle` and `IsHidden` of the host page header, set on your page view |
| `MessageWindow` | Every message and confirmation: `MessageWindow.ConfirmAsync(owner, title, message, confirmText)` |
| `DevicePickerWindow` | Every "choose devices" dialog: search, tags, Select all, 30,000 devices; `ShowAsync` (ids), `ShowOneAsync` (one device), `ShowChoiceAsync` (All devices, tags, chosen devices) |
| `PageDeviceChoice` | The device choice of a page that reads many devices: a toolbar button with the devices icon ("Choose devices", "Selected devices: ..."), see [Core plugins](../core-plugins/) |
| `ComboBox Classes="grouped"` + `GroupedChoice` | A select field with group headers |
| `ComboBox Classes="multiSelect"` + `CheckChoice` / `CheckChoiceList` | A select field with check boxes (`ICheckChoice` items); `CheckChoiceList` offers "All" or chosen items: `<ComboBox Classes="multiSelect" ItemsSource="{Binding Sites.Entries}" PlaceholderText="{Binding Sites.Summary}" />` |

For lists that a grid shows, use `RangeObservableCollection<T>` (`Oadm.Sdk.Client.Collections`): `ReplaceAll`,
`AddRange` and `RemoveAll` raise one change for the whole batch.

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

**A dialog with several sections or a list** uses cards (`Border.card`, `Margin="16,0"`) between title bar and footer,
with a fixed width and height.

**A page** starts with its content. The description goes into the page header with `ui:PageHeader.Subtitle`, never
into a card heading. A status sits directly left of the Save button it reports on.

## Validation

Every validation error appears **directly below its input**: small red text and a red border. Never an error list at
the bottom of a dialog. Form view models derive from `ValidatingViewModel` and register one rule per property:

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

- An error shows once the field was edited or left, or the user tried to submit (`Validation.ShowAll()`); never on an
  untouched form. After loading values, `Validation.Reset(...)`.
- Submit buttons stay disabled while any error exists (`IsFormValid`), with the reason as tooltip (`FormError`).
- An answer of the server that belongs to a field: `Validation.SetServerError(property, message)`.
- An error of a whole table goes directly below the table; an error of one row stays in its status column.

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

Icons are resources of the client. Use them with `DynamicResource` in XAML (`Icon="{DynamicResource Icon.refresh}"`),
and by key without `Icon.` in `IconKey` properties (`IconKey="refresh"`). Some keys:

`add`, `remove`, `refresh`, `restart`, `search`, `settings`, `edit`, `copy`, `export`, `upload`, `download`, `file`,
`folder`, `start`, `stop`, `check`, `close`, `info`, `key`, `lock`, `lockOpen`, `eye`, `eyeOff`, `shield`, `users`,
`user`, `network`, `firmware`, `app`, `video`, `snapshot`, `clock`, `activity`, `toggle`, `zap`, `bell`, `flame`,
`globe`, `grid`, `hardDrive`, `wrench`, `eraser`, `tag`, `plugin`, `clipboardCheck`, `gauge`, `circle`, `thermometer`, `lightbulb`,
`command`, `save`, `send`, `sliders`, `text`.

The full list is in [plugins/README.md](https://github.com/Cacsjep/oadm/blob/main/plugins/README.md#icons). A new
icon goes into `src/Oadm.Theme/Icons.axaml` (a 24 x 24 Lucide stroke geometry).

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
