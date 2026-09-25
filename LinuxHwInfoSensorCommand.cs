using LoupixDeck.Plugin.LinuxHwInfo.Rendering;
using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Pixel;
using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Tiles;
using LoupixDeck.Plugin.LinuxHwInfo.Telemetry;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>
/// Display command that renders Linux sensor readings onto a touch button as pixel tiles (5×7
/// bitmap font, no anti-aliasing). One command carries one sensor; a button's command sequence
/// composes the tile dynamically — the first (rendering) command reads
/// <see cref="CommandContext.SequenceCommands"/> and draws one row per sibling command, up to four.
/// The command name and the "Sensor" parameter are unchanged, so saved buttons keep working.
/// </summary>
internal sealed class LinuxHwInfoSensorCommand(TelemetrySampler telemetry) : IAnimatedDisplayCommand, IDisplayImageCommand
{
    public const string Name = "LinuxHwInfo.Sensor";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Linux Sensor",
        Group = "LinuxHwInfo",
        Icon = "\U000F0379",
        Description = "Render a live Linux hardware sensor reading on a touch button",
        ParameterTemplate = "({Sensor})",
        Parameters = [new CommandParameter("Sensor", typeof(string))],
        // Surfaced per sensor through the dynamic menu.
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public int TargetFps => PixelTile.TargetFps;

    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);

    public AnimationFrameInfo RenderAnimatedFrame(CommandContext ctx, IRenderCanvas canvas, AnimationFrameContext frame) =>
        PixelTile.Render(ctx, canvas, surface => Draw(ctx, surface, TileDrawing.BlinkOn(frame.Elapsed)));

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        PixelTile.Render(ctx, canvas, surface => Draw(ctx, surface, PixelTile.WallClockBlink()));
        return true;
    }

    private void Draw(CommandContext ctx, PixelSurface surface, bool blinkOn)
    {
        TelemetryFrame frame = telemetry.Frame;

        List<SensorRow> rows = [];
        foreach (string? sensorRef in SensorReferences(ctx))
        {
            rows.Add(LinuxReadingBuilder.Build(sensorRef, frame.Sensors));
            if (rows.Count >= SensorTileLayout.MaxRows)
                break;
        }

        SensorTileLayout.Draw(surface, rows, frame, blinkOn);
    }

    /// <summary>
    /// The sensor references to render, in order. On a multi-command button the whole sequence is available:
    /// take the "Sensor" parameter of every sibling that is also a LinuxHwInfo.Sensor command (other commands
    /// in the sequence are ignored). A single-command button reports an empty sequence, so fall back to this
    /// command's own parameter.
    /// </summary>
    private IEnumerable<string?> SensorReferences(CommandContext ctx)
    {
        if (ctx.SequenceCommands.Count > 0)
        {
            foreach (SequenceCommand command in ctx.SequenceCommands)
            {
                if (command.Name != Descriptor.CommandName)
                    continue;

                yield return command.Parameters is { Length: >= 1 } ? command.Parameters[0] : null;
            }

            yield break;
        }

        yield return ctx.Parameters is { Length: >= 1 } ? ctx.Parameters[0] : null;
    }

    public Task Execute(CommandContext ctx) => Task.CompletedTask;
}
