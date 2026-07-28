using GameAgent.Godot.Samples;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

public partial class BasicSample : global::Godot.Control
{
    private global::Godot.Label _status = null!;
    private string _requestId = string.Empty;

    public override void _Ready()
    {
        _status = GetNode<global::Godot.Label>("%Status");
        var runtime = GetNode<GameAgentRuntimeNode>("/root/GameAgentRuntime");
        var fixture = SampleRuntimeFactory.Configure(runtime);
        runtime.RunCompleted += OnRunCompleted;
        runtime.RunFailed += OnRunFailed;

        _requestId = runtime.start_agent_run(
            GodotProtocolVariantMapper.ToDictionary(fixture.Request.Run),
            GodotProtocolVariantMapper.ToArray(fixture.Observations));
        _status.Text = string.IsNullOrEmpty(_requestId)
            ? "Unable to start the sample run."
            : $"Agent run {_requestId} is executing in the background…";
    }

    private void OnRunCompleted(GodotDictionary outcome)
    {
        if (outcome["request_id"].AsString() != _requestId)
        {
            return;
        }

        _status.Text =
            "Completed: "
            + global::Godot.Json.Stringify(outcome["final_output"], "  ");
        global::Godot.GD.Print("GODOT_SAMPLE_PASS");
        if (string.Equals(
                global::Godot.DisplayServer.GetName(),
                "headless",
                StringComparison.Ordinal))
        {
            GetTree().Quit(0);
        }
    }

    private void OnRunFailed(GodotDictionary error)
    {
        if (error["request_id"].AsString() != _requestId)
        {
            return;
        }

        _status.Text =
            $"Failed ({error["code"].AsString()}): {error["message"].AsString()}";
        global::Godot.GD.PushError(
            $"GODOT_SAMPLE_FAIL {error["code"].AsString()}");
        if (string.Equals(
                global::Godot.DisplayServer.GetName(),
                "headless",
                StringComparison.Ordinal))
        {
            GetTree().Quit(1);
        }
    }
}
