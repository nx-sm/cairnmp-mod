using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Marks left on the face.
///
/// A ping is a call for attention that fades in fifteen seconds. A mark is the opposite: a
/// climber says "this hold works" or "not that way", and it stays there for the rest of the
/// session, for everyone, including whoever joins later. Over an evening a rope team ends up
/// writing its own route onto the mountain.
///
/// The host owns the marks so a latecomer receives them all on arrival, and so no client can
/// bury the face under thousands of them.
///
/// B always leaves a mark; Alt + B wipes the face clean, everyone's marks at once. Taking a
/// single mark back by aiming at it again was hard to hit and easy to trigger by accident.
/// </summary>
internal sealed class TrailMarksFeature : MultiplayerFeature
{
    private const GameKey MarkKey = GameKey.B;

    private const int MaxMarksPerPlayer = 8;
    private const float NoticeSeconds = 3f;

    private PerPlayerState<TrailMarkSet> _marks;
    private HostCommand<TrailMarkEdit> _edit;
    private HostCommand<TrailMarksClear> _clear;

    private readonly Dictionary<int, List<WorldPosition>> _known = new();

    public override string Id => "marks";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _marks = feature.PerPlayerState<TrailMarkSet>("set", OnMarksChanged);
        _edit = feature.HostCommand<TrailMarkEdit>("edit", OnEditRequested);
        _clear = feature.HostCommand<TrailMarksClear>("clear", OnClearRequested);

        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnPlayerLeft((playerId, _) => Forget(playerId));
        feature.OnSessionEnded(Reset);
        feature.OnSceneReset(RedrawMarks);
    }

    /// <summary>
    /// A scene boundary is not the end of the session, and the marks belong to the session.
    /// Clearing them here left every mark invisible until its owner happened to edit the set
    /// again, so the whole picture is put back from what we know instead.
    /// </summary>
    private void RedrawMarks()
    {
        Game.World.ClearTrailMarks();
        foreach (var pair in _known)
            Game.World.SetTrailMarks(pair.Key, pair.Value);
    }

    private void TickInput()
    {
        if (!IsConnected) return;

        // Controller: View + LT leaves a mark, View + RT clears the face.
        if (Game.Input.WasPressed(GameInputAction.ClearMarksController))
        {
            _clear.Send(new TrailMarksClear());
            return;
        }
        var place = Game.Input.WasPressed(GameInputAction.PlaceMarkController);

        if (!KeyboardCaptured && Game.Input.WasKeyPressed(MarkKey))
        {
            if (Game.Input.IsAltHeld)
            {
                _clear.Send(new TrailMarksClear());
                return;
            }
            place = true;
        }

        if (!place || !Game.World.TryGetAimPoint(out var point)) return;
        _edit.Send(new TrailMarkEdit(point));
    }

    /// <summary>The host is the only one who edits a set, which keeps the count honest.</summary>
    private void OnEditRequested(HostRequest<TrailMarkEdit> request)
    {
        var positions = new List<WorldPosition>(Current(request.FromPlayerId));
        if (positions.Count >= MaxMarksPerPlayer)
        {
            // The oldest goes rather than refusing: a full set must not turn the key dead.
            positions.RemoveAt(0);
        }
        positions.Add(request.Message.Position);

        request.Publish(_marks, new TrailMarkSet(positions));
    }

    /// <summary>Wipes every climber's marks: the face is shared, so is cleaning it.</summary>
    private void OnClearRequested(HostRequest<TrailMarksClear> request)
    {
        request.Publish(_marks, new TrailMarkSet(new List<WorldPosition>()));
        var others = new List<int>();
        foreach (var pair in _known)
            if (pair.Key != request.FromPlayerId && pair.Value.Count > 0) others.Add(pair.Key);
        request.AfterCommit(() =>
        {
            foreach (var playerId in others)
                _marks.Set(playerId, new TrailMarkSet(new List<WorldPosition>()));
        });
    }

    private IReadOnlyList<WorldPosition> Current(int playerId)
        => _known.TryGetValue(playerId, out var positions)
            ? positions
            : (IReadOnlyList<WorldPosition>)System.Array.Empty<WorldPosition>();

    private void OnMarksChanged(int playerId, TrailMarkSet set)
    {
        var positions = set.Positions ?? new List<WorldPosition>();
        _known[playerId] = positions;
        Game.World.SetTrailMarks(playerId, positions);

        if (playerId != LocalPlayerId) return;
        Game.Hud.ShowMessage("marks",
            positions.Count == 0 ? "Marks cleared." : $"Mark left ({positions.Count}/{MaxMarksPerPlayer}).",
            NoticeSeconds);
    }

    private void Forget(int playerId)
    {
        _known.Remove(playerId);
        Game.World.SetTrailMarks(playerId, System.Array.Empty<WorldPosition>());
    }

    private void Reset()
    {
        _known.Clear();
        Game.World.ClearTrailMarks();
    }
}

internal sealed class TrailMarkEdit : IPacket
{
    public TrailMarkEdit() { }
    public TrailMarkEdit(WorldPosition position) => Position = position;

    public WorldPosition Position;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Position.X);
        writer.Write(Position.Y);
        writer.Write(Position.Z);
    }

    public void Deserialize(BinaryReader reader)
        => Position = new WorldPosition(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

/// <summary>Asks the host to wipe every mark from the face. Carries nothing.</summary>
internal sealed class TrailMarksClear : IPacket
{
    public void Serialize(BinaryWriter writer) => writer.Write((byte)1);
    public void Deserialize(BinaryReader reader) => reader.ReadByte();
}

internal sealed class TrailMarkSet : IPacket
{
    public TrailMarkSet() { }
    public TrailMarkSet(List<WorldPosition> positions) => Positions = positions;

    public List<WorldPosition> Positions = new();

    public void Serialize(BinaryWriter writer)
    {
        var positions = Positions ?? new List<WorldPosition>();
        writer.Write((byte)positions.Count);
        foreach (var position in positions)
        {
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
        }
    }

    public void Deserialize(BinaryReader reader)
    {
        var count = reader.ReadByte();
        Positions = new List<WorldPosition>(count);
        for (var index = 0; index < count; index++)
            Positions.Add(new WorldPosition(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }
}
