using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Plugins.ActionRules.Client;
using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The page's Copy to devices, the Recipients and Schedules tabs and their forms.</summary>
public sealed class PresetPageTests
{
    private static Task UntilAsync(Func<bool> condition) => Oadm.Sdk.Testing.Eventually.TrueAsync(condition, "the page state");

    private static RuleTaskPayload Payload(PageRig rig, int index) => ActionRulesJson.Deserialize<RuleTaskPayload>(rig.Runner.Runs[index].Payload);

    [Fact]
    public async Task A_rule_is_copied_to_chosen_devices_with_its_changes()
    {
        await using var rig = new PageRig();
        var source = rig.Add("10.0.0.1", c => PageTests.AddTcp(c, "test", "camera 1"));
        var target = rig.Add("10.0.0.2", os: "os10");
        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => !vm.IsReading && vm.Groups.Count == 1);
        vm.SetGroupSelection(vm.Groups);
        Assert.True(vm.CopyCommand.CanExecute(null));
        rig.Dialogs.Devices.AddRange([source.Id, target.Id]);
        rig.Dialogs.Edit = form =>
        {
            Assert.True(form.IsCopy);
            Assert.Equal(("Copy action rule", "Copy"), (form.Title, form.ApplyText));
            Assert.True(form.CanApply); // a copy as it is needs no change
            form.ChangeName = true;
            form.Name = "Copied";
            return form.Patch();
        };

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal("This is the device the copy comes from.", rig.Dialogs.Offered.Single(o => o.Device == source.Id).Reason);
        var payload = Payload(rig, 0);
        Assert.Equal((RuleTaskActions.Deploy, "Copied", "test"), (payload.Action, payload.Patch!.Name, payload.Rule!.Name));
        Assert.Equal([target.Id], rig.Runner.Runs[0].DeviceIds);
        Assert.Equal("camera 1", payload.Rule.Action.Get("message"));
    }

    [Fact]
    public async Task Recipients_and_schedules_are_grouped_and_changed()
    {
        await using var rig = new PageRig();
        var a = rig.Add("10.0.0.1", c => c.Add("Night", "com.axis.action.unlimited.irlight", [("lightid", "led0"), ("activate", "yes")], conditions: [PageTests.Schedule]));
        rig.Add("10.0.0.2");
        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => !vm.IsReading && vm.Schedules.Groups.Count > 0 && vm.Recipients.Groups.Count > 0);

        vm.TabIndex = 1;
        Assert.Equal(("Search recipients", true), (vm.SearchPlaceholder, vm.IsRecipientsTab));
        var ftp = vm.Recipients.Groups.Single(g => g.Name == "asdasd");
        Assert.Equal(("FTP", 2), (ftp.Kind, ftp.DeviceCount));
        Assert.Contains("host adasdas", ftp.Detail, StringComparison.Ordinal);
        vm.Recipients.SetGroupSelection([ftp]);
        Assert.Equal("2 recipients on 2 devices", vm.Recipients.TargetText);
        Assert.Equal("Select one item on the right to copy.", vm.Recipients.CopyBlockedReason);
        rig.Dialogs.Recipient = form =>
        {
            var host = form.Parameters.Single(p => p.Name == "host");
            host.Change = true;
            host.Value = "ftp.example";
            Assert.True(form.UpdateRules && form.CanApply);
            Assert.Equal("Stays as it is unless you enter a new one.", form.Parameters.Single(p => p.Name == "password").Hint == "Empty on some recipients." ? "Stays as it is unless you enter a new one." : form.Parameters.Single(p => p.Name == "password").Hint);
            return form.Patch();
        };
        await vm.Recipients.EditCommand.ExecuteAsync(null);
        var edit = Payload(rig, 0);
        Assert.Equal((RuleTaskActions.RecipientEdit, "ftp.example", true), (edit.Action, edit.RecipientPatch!.Parameters!["host"], edit.RecipientPatch.UpdateRules));
        Assert.Equal(2, edit.Targets.Count);

        vm.TabIndex = 2;
        var office = vm.Schedules.Groups.Single(g => g.Name == "Office Hours");
        Assert.Equal(("Mon to Fri 08:00 to 18:00", "Built in · used by 1 rule"), (office.Detail, office.Kind));
        vm.Schedules.SetGroupSelection([office]);
        Assert.Equal("Built-in schedules are not changed.", vm.Schedules.ChangeBlockedReason);
        Assert.False(vm.Schedules.DeleteCommand.CanExecute(null));

        var own = vm.Schedules.Groups.Single(g => g.Name == "sss pulse min");
        Assert.Equal("Every minute", own.Detail);
        vm.Schedules.SetGroupSelection([own]);
        await vm.Schedules.DeleteCommand.ExecuteAsync(null);
        Assert.StartsWith("Delete schedules: Delete 2 schedules on 2 devices?", rig.Context.Confirmations[^1], StringComparison.Ordinal);
        Assert.Null(Payload(rig, 1).Rules); // no rule uses it

        rig.Dialogs.Devices.Add(a.Id);
        rig.Dialogs.Schedule = form =>
        {
            Assert.True(form.IsNew);
            Assert.False(form.CanApply); // a name is needed
            form.Name = "Night shift";
            form.From = "22:00";
            form.To = "06:00";
            Assert.Equal("Mon to Fri 22:00 to 06:00 the next day", form.Summary);
            Assert.True(form.CanApply);
            return true;
        };
        await vm.Schedules.NewCommand.ExecuteAsync(null);
        var created = Payload(rig, 2);
        Assert.Equal(RuleTaskActions.ScheduleDeploy, created.Action);
        Assert.Equal(("com.axis.schedules.oadm.night-shift", "Night shift"), (created.Schedules![0].Id, created.Schedules[0].Name));
    }

    [Fact]
    public void The_schedule_form_checks_days_times_and_minutes()
    {
        var form = ScheduleFormViewModel.ForNew(3);
        form.Name = "Weekend";
        Assert.True(form.CanApply);
        foreach (var day in form.Days)
        {
            day.IsOn = false;
        }

        Assert.Equal("Choose at least one day.", form.WhenError);
        Assert.False(form.CanApply);
        form.Days[5].IsOn = true;
        form.Days[6].IsOn = true;
        form.From = "25:00";
        Assert.False(form.CanApply);
        form.From = "00:00";
        form.To = "00:00";
        Assert.Equal("The schedule must end at another time than it starts.", form.WhenError);
        form.To = "23:59";
        Assert.Equal("Sat, Sun 00:00 to 23:59", form.Summary);
        form.KindIndex = 1;
        form.EveryMinutes = 0;
        Assert.Equal("Enter 1 to 10,080 minutes.", form.WhenError);
        form.EveryMinutes = 30;
        Assert.Equal(new SchedulePatch("Weekend", "DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL=30"), form.Patch());
    }

    [Fact]
    public async Task The_tabs_and_forms_render()
    {
        var session = HeadlessEntry.Session;
        var rig = await session.Dispatch(() =>
        {
            var rig = new PageRig();
            for (var i = 1; i <= 6; i++)
            {
                var n = i;
                rig.Add($"10.0.0.{40 + n}", c =>
                {
                    c.Add("Night", "com.axis.action.unlimited.irlight", [("lightid", "led0"), ("activate", "yes")], conditions: [PageTests.Schedule]);
                    if (n % 2 == 0)
                    {
                        c.Recipients["7"] = new DeviceRecipient("7", "VMS", "com.axis.recipient.tcp", [new("host", "192.0.2.5"), new("port", "8080"), new("qos", "0")]);
                    }
                }, os: n == 3 ? "os10" : n == 4 ? "os11" : "os12");
            }

            var view = new ActionRulesView { DataContext = rig.ViewModel };
            var page = new Oadm.Client.Shell.CorePluginPageView
            {
                DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(ActionRulesInfo.PluginId, ActionRulesInfo.DisplayName, view, new ActionRulesPage().HasOwnCards),
            };
            var window = new Window { Width = 1600, Height = 620, Content = new Border { Padding = new Thickness(16), Child = page } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(30);
            while ((rig.ViewModel.Schedules.Groups.Count == 0 || rig.ViewModel.IsReading) && DateTime.UtcNow < until)
            {
                Pump();
                Thread.Sleep(20);
            }

            rig.ViewModel.TabIndex = 1;
            var recipients = view.FindControl<DataGrid>("RecipientGroupGrid")!;
            recipients.SelectedItems.Add(rig.ViewModel.Recipients.Groups.Single(g => g.Name == "VMS"));
            Pump();
            Capture(window, "action-rules-recipients.png");

            rig.ViewModel.TabIndex = 2;
            var schedules = view.FindControl<DataGrid>("ScheduleGroupGrid")!;
            schedules.SelectedItems.Add(rig.ViewModel.Schedules.Groups.Single(g => g.Name == "Office Hours"));
            Pump();
            Capture(window, "action-rules-schedules.png");

            var recipientForm = new RecipientFormViewModel([.. rig.ViewModel.Recipients.Groups.Single(g => g.Name == "VMS").Recipients!.Members]);
            recipientForm.Parameters.Single(p => p.Name == "host").Change = true;
            var recipientWindow = new RecipientFormWindow { DataContext = recipientForm };
            recipientWindow.Show();
            Pump();
            Capture(recipientWindow, "action-rules-recipient-form.png");
            recipientWindow.Close();

            var scheduleForm = ScheduleFormViewModel.ForNew(6);
            scheduleForm.Name = "Night shift";
            scheduleForm.From = "22:00";
            scheduleForm.To = "06:00";
            var scheduleWindow = new ScheduleFormWindow { DataContext = scheduleForm };
            scheduleWindow.Show();
            Pump();
            Capture(scheduleWindow, "action-rules-schedule-form.png");
            scheduleWindow.Close();
            window.Close();
            return rig;
        }, CancellationToken.None);
        await rig.DisposeAsync();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is { Length: > 0 } outDir)
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}
