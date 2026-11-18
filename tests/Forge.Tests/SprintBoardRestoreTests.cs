using System.Reflection;
using System.Text.Json;
using Fluxor;
using Forge.Dashboard.Components.Pages;
using Forge.Dashboard.Features.AppShell;
using Forge.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Forge.Tests;

public sealed class SprintBoardRestoreTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task DelayedPreviousProjectCannotReplaceCurrentBoardLocation()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new State("old");
        var page = Create(shell, new Browser(project => project == "old" ? pending.Task : Task.FromResult(Location("new"))));
        var old = Restore(page);
        shell.Value = new() { CurrentProjectId = "new" };
        Assert.True(await Restore(page));
        pending.SetResult(Location("old"));
        Assert.False(await old);
        Assert.Equal("new", Field<string>(page, "_loadedProject"));
        Assert.Equal("new-sprint", Field<string>(page, "_selectedSprintId"));
        Assert.Equal("new-cursor", Field<string>(page, "_memberCursor"));
        page.Dispose();
    }

    [Theory]
    [InlineData("sprint")]
    [InlineData("lane")]
    [InlineData("dispose")]
    public async Task UserSelectionOrDisposalInvalidatesPendingRestoration(string action)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = Create(new State("forge"), new Browser(_ => pending.Task));
        var restore = Restore(page);
        if (action == "dispose")
        {
            page.Dispose();
        }
        else
        {
            // An existing refresh is allowed to coalesce the user selection; no renderer/network required.
            typeof(Sprints).GetField("_refreshing", Private)!.SetValue(page, true);
            var method = action == "sprint" ? "SelectSprintAsync" : "SelectLaneAsync";
            var value = action == "sprint" ? "chosen-sprint" : "Queued";
            await (Task)typeof(Sprints).GetMethod(method, Private)!.Invoke(page, [new ChangeEventArgs { Value = value }])!;
        }
        pending.SetResult(Location("stale"));
        Assert.False(await restore);
        Assert.Null(Field<string?>(page, "_memberCursor"));
        Assert.False(Field<bool>(page, "_restoreBoard"));
        if (action == "sprint") Assert.Equal("chosen-sprint", Field<string>(page, "_selectedSprintId"));
        if (action == "lane") Assert.Equal("Queued", Field<string>(page, "_lane"));
        if (action != "dispose") page.Dispose();
    }

    private static string Location(string prefix) => JsonSerializer.Serialize(new
    {
        sprintId = prefix + "-sprint", lane = "Queued", memberCursor = prefix + "-cursor"
    });

    private static Sprints Create(State shell, Browser browser)
    {
        var page = new Sprints();
        typeof(Sprints).GetProperty("ShellStore", Private)!.SetValue(page, shell);
        typeof(Sprints).GetProperty("Events", Private)!.SetValue(page, new DashboardEventStream(null!));
        typeof(Sprints).GetField("_boardBrowser", Private)!.SetValue(page, browser);
        return page;
    }

    private static Task<bool> Restore(Sprints page)
        => (Task<bool>)typeof(Sprints).GetMethod("RestoreProjectLocationAsync", Private)!.Invoke(page, null)!;

    private static T Field<T>(Sprints page, string name)
        => (T)typeof(Sprints).GetField(name, Private)!.GetValue(page)!;

    private sealed class State(string project) : IState<AppShellState>
    {
        public AppShellState Value { get; set; } = new() { CurrentProjectId = project };
        public event EventHandler? StateChanged { add { } remove { } }
    }

    private sealed class Browser(Func<string, Task<string>> read) : IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);
        public async ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args)
        {
            if (name != "begin") return default!;
            return JsonSerializer.Deserialize<T>(await read((string)args![0]!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
}
