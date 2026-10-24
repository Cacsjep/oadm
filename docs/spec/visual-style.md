# Visual style

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Reference: MultiDrive (Avalonia showcase), https://multidrive.io/.
Dark by default, calm and spacious, no gradients inside the app (one exception: the soft glow of the start splash).
**Appearance** Dark or Light per computer (Settings > This computer, `ClientSettings.Appearance`, takes effect after the
client restarts: "Takes effect after the client restarts" below the select while it differs from the running one). The
colors live in `src/Oadm.Theme/Colors.Dark.axaml` and `Colors.Light.axaml` (same keys, `ThemeColorsTests`);
`Oadm.Theme.OadmColors` merges the chosen one into `OadmTheme.axaml` (brushes and styles, shared), read in `App.Initialize`
before the styles load (`JsonClientSettingsStore.ReadAppearance`, `AppOptions.Appearance` overrides it), and the Fluent
theme variant follows. The color values below are the dark ones. Video stays dark in both: plates over the picture use
`Oadm.OverlayBrush` (dark or white translucent with the normal text), the autofocus dimming `Oadm.VideoDimBrush`. Headless
tests render light with `OADM_TEST_APPEARANCE=Light` (client tests).

- Start splash (approved mockup `mockups/loading/index.html`): `Shell/SplashWindow` (borderless,
  520x560, centered) with `Controls/SplashLogo` (the app icon built from seven hexagons: the outer six fly in one after another,
  each drawn as an accent outline that fills; then the center hexagon, the lens ring and the lens opening like an iris; a soft
  glow (`Oadm.SplashGlowBrush`) breathing behind it, one scan line sweep, one lens blink; complete after 2.3 s), the wordmark
  "OADM" whose letter spacing pulls together, "Open AXIS Device Management", the shared progress bar and the real loading step
  (`SplashViewModel`: Starting, Connecting to <server> (+ resuming a remembered login), Loading devices (main streams started,
  waits for the first device list, at most 15 s), Ready). `AppShell.StartupAsync` keeps it at least 2.7 s (logo and wordmark
  complete), then opens the login window (nobody remembered; the login view model is already initialized) or the main
  window behind it (the splash on top, still showing "Ready"), waits for that window's first frame (at most 3 s) and only
  then fades the whole splash window out (0.35 s); a login does the same, the login window stays until the main window
  drew. Log out shows the login window directly, without the splash. Tests: `SplashTests`; `StartupStallTests` runs the
  start through `AppShell` against the fake server with a stall probe (`UiStallProbe`: how long a Render job waits), Perf
  with 30,000 devices: the longest UI stall until "Ready" stays under 400 ms (measured about 45 ms, before 420 to 590 ms;
  `OADM_STARTUP_PLUGINS=<folder>` also loads client plugins).

- Window: starts maximized. Custom title bar (ExtendClientAreaToDecorationsHint), app name
  top-left, the user and Log out top-right before the window buttons, background near-black `#141414`. Caption button hover
  is a neutral grey slightly lighter than the title bar (never the system blue/accent); the
  close button hovers red like Windows. No connection indicator in the title bar; a banner
  appears only while the server is unreachable.
- Left navigation rail: expanded by default (collapsed: icons only, ~56 px, tooltips with the label),
  expand/collapse toggle at the bottom of the rail; expanded ~220 px with icon + label.
  Selected item as rounded pill `#1C1C1C` (hover `#181818`, the icon wobbles once on hover, `Oadm.NavSelectedBrush` / `NavHoverBrush`, quieter than buttons) with a short accent bar at its left edge like WinUI's NavigationView (3 x 16 px,
  rounded; `Button.nav` template, `Border#PART_Indicator`). Plugin pages (not host pages) have a context menu
  **Open in new window** on their rail entry: the plugin creates another, independent view (own context, streams, state)
  shown in `Shell/PluginWindow` (an empty title bar: the page header names the page; window title
  "<page> · OADM" for the task bar; page padding, not modal, as many as wanted); the windows close
  with the main window, on logout and when the plugin is turned off; a closed window (or a removed rail page) disposes its
  page: the plugin context drops every `DevicesChanged` subscription and the view's DataContext is disposed when it is
  `IDisposable`; the main window unsubscribes from the shell view model when it closes (a new main window per login) (`MainWindowViewModel.PluginWindowRequested` /
  `ClosePluginWindowsRequested`; tests `PluginWindowTests`). Entries top: Devices, **Compliance** (built in, right below Devices, no group; while the role may see
  compliance), then one per Core plugin
  page, grouped by `ICorePlugin.Group` (`CorePluginGroup`, in this order: Servers (NTP, DHCP), Automation (VAPIX
  Commander, Device triggers, Action Rule Editor), Security (PKI, Hardening scan), Monitoring (Live View, Dashboards, Metadata Monitor, Image Health Dashboard), Reporting (Snapshot report; System report has
  no page), Maintenance (Edge Recording Export, Edge storage), Integrations, Utilities (Web View), Extensions (default, third-party)): only groups
  with pages appear, the server's order inside a group; expanded rail = a small grey header (`TextBlock.navGroup`) above
  each group (in the lighter accent color), collapsed = a separator at each group start; the pages scroll when they do not fit. Pinned bottom, top to bottom: Plugins with a divider below it (it changes what the whole client offers), Users,
  Credentials (these three Admin only: hidden for operators, follow the session role live), Logs, Settings, About (icons
  `plugin`, `users`, `key`, `logs`, `settings`, `info`), then the expand toggle. The logged-in user (an initial avatar and
  the name, `Button.titleUser`, opens "Your role") and Log out (icon-only `ui:ToolbarButton`) sit at the right of the title
  bar, next to the system window buttons (`Oadm.Theme.Shell.TitleBarLayout` keeps clear of them: `ContentMargin`, or `ContentMarginWithoutMaximize` for the OADM Site Node app; title bars use the theme style `Border.titleBar`: right on Windows and Linux, left on
  macOS, where the app name moves past them; Avalonia reports only the title bar height, not where the buttons are). Keys = `HostPages` (`users`, `credentials`, `plugins`, `logs`, `settings`, `about`); opening an
  admin page as operator does nothing, a hidden page that was open falls back to Devices. No Tasks page. Every page
  header (core plugin pages, Users, Credentials, About, Settings) is the shared `Controls/PageTitleBar` (title, grey
  side subtitle, trailing content); host pages never repeat the page title in a card heading. A plugin page may leave the header row out
  (`ui:PageHeader.IsHidden`, Live View: every pixel for the video).
- Content in rounded cards: background `#181818` (only slightly lighter than the window),
  corner radius 8 (Microsoft Fluent geometry: 8 for cards, menus and flyouts, 4 for buttons, inputs and rows inside a page), padding 20-24, 16 px gap. The device grid sits in one card, the tasks
  pane in a second card below it, separated by a draggable splitter so the user can resize
  the tasks pane; the split is persisted per client.
- Accent violet `#6C5CE7` (hover `#7D6EF0`), used for selection, progress bars, primary
  buttons and status pills. Secondary accent teal `#7FC8D0`.
- Text: primary `#F2F2F2`, secondary `#9A9A9A` (labels like "Source", column headers).
  Font Inter (Avalonia.Fonts.Inter), whole-pixel sizes only (fractional sizes blur): 12 px body, 10 px small, 18 px card titles, 22 px page titles, semibold titles; every window renders text with subpixel anti-aliasing, strong hinting and pixel-aligned baselines (App.ApplyCrispText). Page subtitles sit right of the page title, small, grey, baseline aligned.
- Status is never a chip: everywhere a status or result is shown (device certificate columns,
  tasks, task steps, logs, add page, plugin dialogs) use `ui:StatusChip` = a small icon colored
  by the status (check ok green, warning amber, error red, running violet and spinning, information blue "i" (`IsInfo`,
  something happened, nothing to check), neutral grey) with plain text in the normal text color to its right, plus optional detail text
  (e.g. the error message). Colour marks exceptions: the device grid's first-column
  device icon is neutral for OK and colored only for warning and error statuses, the Status column is plain text, and
  certificates that are fine (more than 30 days left, Trusted) are plain text without an icon. Tasks pane: the Status column shows only the
  colored icon, with the error or warning message right of it when there is one; finished tasks
  show no progress bar. Count badges stay a small solid violet pill.
- Window chrome: title bar and caption buttons are 32 px high (caption buttons 46x32), exactly
  like the Windows default. Main window minimum size 1200x640.
- **Small and scaled screens** (Full HD at 125 % leaves about 1536 x 816 logical pixels): every window, host and plugin
  dialogs alike, is fitted to the screen it opens on (`Oadm.Theme.Shell.WindowFit`, also used by the site node app, attached in `App`, applied once the window is placed: dialogs open next to
  their owner, also on another monitor, plugin windows on the monitor of the main window; a window larger than the
  work area shrinks to it (no margin: `WindowFit.Margin` = 0, a margin capped Maximize of the main window short of the
  screen after a minimize and widened the title bar padding), its minimum and maximum follow, then it is centered or
  moved back onto the screen; `WindowFitTests`). Toolbars of buttons are `ui:ToolbarOverflowPanel` (entries that do not fit go behind a More
  button), input rows that must not leave the card wrap (`WrapPanel.inputRow`), grid columns keep readable minimum
  widths and the grid scrolls sideways. Check a new page with the layout survey (`LayoutSurveyTests`,
  `OADM_LAYOUT_SURVEY=1`, `OADM_LAYOUT_SURVEY_SIZE=1280x672`, screenshots and a list of controls that leave the window).
- Dialogs (every dialog window incl. `ui:MessageWindow`, the device picker and the login
  window): no system title bar (`WindowDecorations="BorderOnly"`); the first row is `ui:DialogTitleBar`, the title as a
  heading and an X top right that closes the window, aligned with the content. Modal, not movable and fixed size
  (`CanResize="False"`: a resizable border-only window shows a white resize strip on top on Windows), like a dialog on a
  web page. The main window and plugin windows keep the custom title bar.
- Text fields, number fields and select fields: text vertically centered (measured; select
  fields need the Inter correction, text boxes do not). A focused field has the accent border (select and number
  fields too, also after a click). The mouse wheel over a closed select field or a number field scrolls the page and never
  changes the value (`Infrastructure/WheelScroll`, attached in `App`, so plugin views too; test `WheelScrollTests`).
- Icons: one outline icon set (Fluent System Icons / Lucide geometry), monochrome
  `#CFCFCF`, 16 px. Toolbar uses icon + text buttons, not ADM's icon-only bar.
- All colors in `src/Oadm.Theme/Colors.Dark.axaml` / `Colors.Light.axaml`, brushes and sizes in `src/Oadm.Theme/OadmTheme.axaml`
  (the client's `Themes/OadmTheme.axaml` includes it plus the client-only styles: tag groups, `TagChipList`, `TagColorPicker`); no
  hard-coded colors in views or code (a new color gets a key in both files). Exceptions: the splash glow gradient and the
  Fluent palettes in `OadmTheme.axaml` (accent based, the same in both), fallbacks in code when a key cannot be found.

## Shared controls

Every shared control (Coding Rules: "reuse UI components") and what it is for. Existing shared ones: `IconLabel` (every icon + text row: navigation
rail, dialogs), `ToolbarButton` (every toolbar button) and `ToolbarSeparator`, `ToolbarOverflowPanel` (every row of
toolbar buttons: what does not fit goes behind its own More button; the search box stays outside it), `SearchBox`
(every search field), `OadmIcon`, and for
dialogs `DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip` (status chip),
`FileRow` (chosen file + "Choose file"; `IsSingleButton`: one wide button that shows the chosen file's name, the
compliance check form), `ProgressRow` (progress bar + status text), `LoadingOverlay` (a dialog that waits for a read: steps and cards hidden, ring and text in the middle, Cancel stays usable),
`MessageWindow` (every message box and confirmation popup, host and plugins; dialogs never show
inline "I understand" risk sections; line breaks start a new line, text between `**` is bold; `ConfirmWithChoiceAsync`: one
select below the message, e.g. the Reason of Revoke, `danger` for a red confirm button of an action that cannot be undone),
`CertificateWindow` + `CertificateDetails` (every certificate shown in OADM's own window: EST enrollments, PKI's list of
issued certificates, the 802.1X log; validity chip or the revocation, issued to and by, key and signature, serial,
fingerprint, uses, names, all selectable; the server describes the certificate with the SDK's `CertificateDescriber`, the
802.1X log describes the DER it reads with it in the client),
`IssuedCertificateList` + `IssuedCertificateListViewModel` (every list of the certificates the OADM CA issued: the PKI
Certificates tab and the certificate choice of the 802.1X server's Revoke; the page's buttons left, the filters state,
use, issued by and the search right (it waits 300 ms for the typing to stop), progress row, virtualized grid with several
rows chosen in one step and a double click for View certificate, the empty line, the overlay of the first read, the error
and the pages of 500 below; `IIssuedCertificateSource` says where it reads, `IssuedCertificateList.ConfirmRevokeAsync` is
the revoke confirmation with the reason, `IssuedCertificateTexts` the texts),
`PasswordBox` (every password field: TextBox with bullet mask and eye button "Show password" /
"Hide password", icons `Icon.eye` / `Icon.eyeOff`; never a `TextBox` with `PasswordChar`), `CodeView`
(every read-only code / response display: JSON and XML pretty-printed with 2 spaces and highlighted,
param.cgi `key=value` highlighted with `# Error` lines red, `Language` Auto/Json/Xml/KeyValue/Plain with
Auto from the content type then the text, `IsFormatted` false = exact text; unparsable text plain, above
512 K characters plain; monospace `Oadm.FontFamilyMono`, no wrapping, selectable and copyable; colors
`Oadm.Code.*Brush`; logic in `CodeText` without UI), `CodeEditor` (every editable code field, a monospace text box for
typing, caret, selection and undo with the same text drawn colored on top, aligned and scrolled with it; `Language`
Json / Xml / KeyValue / Plain; never re-indented; the coloring of `CodeView` through `CodeHighlight`, also for JSON and
XML that do not parse yet (`CodeText.Prepare(colorUnparsed: true)`: typing, an HTML body with several elements)), `FormField` (every labeled form row: label,
input, error or hint below the input; classes `wide` and `inline`), `StatCard` (every monitoring count card: status icon +
title, large count, small detail line; `IsOk` / `IsWarning` / `IsError` color icon and count, set only while the count
needs attention; a card that filters a list sits in `Button.statCardButton`, the chosen one with `selected`), `DevicePickerWindow` + `DevicePickerViewModel` (every "choose devices" dialog, every managed device in address order, search over address, model, MAC and host name, Select all / Select none for what the
search shows, the count "12 of 4,812 devices chosen", chosen devices can be unchosen, devices that cannot be used greyed
with the reason; virtualized, 5,000 devices tested; while its `Accept` runs (the host saves) Cancel, Escape and the X wait (`IsAccepting`); `DevicePickerWindow.ShowAsync` returns the ids or null; `ShowOneAsync` (`DevicePickerOptions.SingleDevice`: checking a device
unchecks the one before, no Select all / none) returns one id; with
`DevicePickerOptions` a left column offers the device of the event, All devices, or Chosen devices and tags with the tag
list (the devices of a chosen tag show checked with "Tag <name>"), `ShowChoiceAsync` / `DevicePickerViewModel.Choice`
return a `DeviceChoice` {Kind All / EventDevice / Chosen, DeviceIds, Tags}; with tags it lists the devices' tags, every
tag of the initial choice and `DevicePickerOptions.TagNames` (the rule editor passes the defined tags), count 0 when no
device has it; summary "1 tag: 42 of 5,000 devices" / "2 tags and 3 devices: ..."; the device group editor,
`DevicePickerOptions.Group` / `DeviceGroupPickerOptions`: left **Sites** ("This server" first) and **Tags** with device
counts, the devices a chosen site or tag covers checked and locked with "Site Building B" / "Tag PTZ" / "This server",
single devices tickable, a row link **Except** / **Include** (an excepted device shows "Excepted from Site Building B"),
the live count "412 of 4,812 devices", search also over the site name, result `DeviceGroupChoice` (serials of devices
not listed are kept); the window's constructor takes a form shown on top of the card, `AcceptButton` and `Accept` (the
window closes only when it returns true), and `DevicePickerViewModel.ErrorText` shows a refusal directly below the
list; 30,000 devices tested) and
`PageDeviceChoice` (the device choice of every page that reads many devices: the purple "Selected devices" button text,
All devices / tags / chosen devices resolved now, the Devices page selection to start with, the last choice kept on this
computer; Action Rule Editor, Edge storage, Image Health Dashboard, Hardening scan, Snapshot report) and
`DeviceIcon` (every device icon in a list: the category icon `Icon.device.*`, neutral when OK, amber for Credentials
required / Password not set / Certificate changed, red for Unreachable, `ShowStatusToolTip` = the status message on
hover; device grid, Web View, Live View), `DeviceTree` + `DeviceTreeModel` (every devices-by-tag tree: tag groups in
name order with a chevron, "No tag" last, a device under each of its tags, sorted by address, search over address,
model, MAC, host name and tags, optional filter, one reset per rebuild; Web View, Live View), `PageTab` (every tab of a
page with tabs: title, close button, the active one underlined; theme `Border.browserTab`; without a close command a
fixed tab: the PKI page, the OADM Site Node app), `TileGrid` + `TileFrame` (every
grid of tiles that fills its page: cells of `Oadm.Sdk.Layout.TileLayout` (size, smallest tile, drop size, most tiles;
the same rules on the server), a frame per `ITileGridItem` of an `ITileGridSource`, while editing the cell lines and a
frame per tile to move, resize and remove it, the selected tile (`ITileGridItem.IsSelected`) marked, `FrameClass` a theme class of the frame (`valueTile`: no video background), `CopyCommand` a copy icon left of the X (shown while it can run; the X is red, `Button.toolbar.danger`), `ShowPreview` where a
dragged item lands, `ShowSelectionArea` a selection rectangle, `TileLayout.PlaceSized` the first free place of a size,
`ITileGridSource.CopyText` the copy icon's tooltip; theme `Panel.tile`, `Border.tileEdit`, `Border.tileHover`, `Panel.tile.selected`, `Panel.tile.valueTile`, `Border.dropPreview`;
Live View, Dashboards), `InfoIcon` (every "what does this mean" explanation: an info icon whose text shows on hover and click; `FormField.Info`
puts one right of the input instead of a long hint), `WizardSteps` + `WizardStepItem` (the step
row of every wizard: numbered dot and name, thin line between, current accent, done with a check, a step opens when
every step before it is complete; theme `Button.wizardStep`, `Border.wizardDot`, `TextBlock.wizardLabel`,
`Border.wizardLine`), `GroupedChoice` with `ComboBox.grouped` (every grouped select field: grey group headers that cannot be chosen, the
choices indented below them, like the event list of a device), `ICheckChoice` / `CheckChoice` / `CheckChoiceList` with
`ComboBox.multiSelect` (every select field with check boxes: the closed box names what is ticked through
`PlaceholderText`, each entry a check box with an optional grey second line, a click keeps the list open;
`CheckChoiceList` = "All ..." or chosen items, nothing ticked = none; the NTP server's sites, the OADM Site Node app's
networks).
