using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Views;
using Xunit;

/// <summary>The panel itself, only far enough to prove the XAML loads and a provider reaches the
/// rows. The merging is covered by ProviderViewTests; this is the part that a typo in the markup
/// would break and no model test would notice.</summary>
public class ProvidersPanelTests
{
    private static ProviderRecord Record(string name, string key, string value)
        => new(name,
            new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [key] = new TextValue(value) }),
            null, null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    [Trait("Category", "Desktop")]
    public void A_Remembered_Provider_Reaches_The_Rows_And_The_Test_Box()
    {
        OnStaThread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "deskwall-tests", "panel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var records = new List<ProviderRecord> { Record("build", "status", "green") };
                var model = new ProvidersModel(() => records, r => records = r.ToList(), () => [], dir);
                using var bus = new EventBus(SystemClock.Instance, EventBus.DefaultCoalesce, autoWake: false);
                var panel = new ProvidersPanel(model, bus);

                IReadOnlyList<ProviderRecord>? pushed = null;
                panel.ProvidersChanged += r => pushed = r;
                panel.Reload();

                var paths = panel.Fields.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag!).ToList();
                Assert.Contains("build.data.status", paths);
                Assert.Contains("build.ageSeconds", paths);
                // The seed is what the user edits and sends, so it has to be a valid envelope.
                var (parsed, why) = EventEnvelopeParser.Parse(panel.TestJson.Text);
                Assert.Null(why);
                Assert.Equal("build", parsed!.Source);
                // And the window gets the records, which is what puts them in the binding picker.
                Assert.NotNull(pushed);
                Assert.Equal(["build"], pushed!.Select(r => r.Name));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        });
    }

    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var noDesktop = false;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TypeInitializationException) { noDesktop = true; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Dispatcher")) { noDesktop = true; }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (noDesktop) return;
        failure?.Throw();
    }
}
