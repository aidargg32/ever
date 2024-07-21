using System.Threading;
using Content.Server.Atlanta.GameTicking.Rules.Components;
using Content.Server.Atlanta.Player;
using Content.Server.Atlanta.Player.Events;
using Content.Server.Atlanta.Supply.Events;
using Content.Server.Atlanta.Waves.Events;
using Content.Server.GameTicking.Rules;
using Content.Server.KillTracking;
using Content.Server.Mind;
using Content.Server.Objectives;
using Content.Server.RoundEnd;
using Content.Server.Shuttles.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using MistPlayerComponent = Content.Shared.Atlanta.Mist.Components.MistPlayerComponent;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server.Atlanta.GameTicking.Rules;

/// <summary>
/// Mist game rule
/// </summary>
public sealed class MistGameRuleSystem : GameRuleSystem<MistGameRuleComponent>
{
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        // rule
        SubscribeLocalEvent<MistGameRuleComponent, ComponentStartup>(OnRuleStartup,
            after:
            [
                typeof(ExtendedLateJoinRuleSystem), typeof(SupplyPointsRuleSystem),
            ]);

        // players
        SubscribeLocalEvent<PlayerSpawnAfterEvent>(OnAfterPlayerSpawner);
        SubscribeLocalEvent<KillReportedEvent>(OnKillReported);

        // waves
        SubscribeLocalEvent<WaveStartsEvent>(OnWaveStart);
        SubscribeLocalEvent<AllWaveEnemiesDead>(OnWaveEnemiesDead);

        // ending
        SubscribeLocalEvent<MistGameRuleComponent, ObjectivesTextGetInfoEvent>(OnObjectivesTextGetInfo);
        SubscribeLocalEvent<MistGameRuleComponent, ObjectivesTextPrependEvent>(OnObjectivesTextPrepend);
        SubscribeLocalEvent<RoundEndSystemChangedEvent>(OnRoundEnd);

        _sawmill = _logManager.GetSawmill("Mist Game Rule");
    }

    private void OnKillReported(ref KillReportedEvent ev)
    {
        var player = ev.Entity;
        _sawmill.Debug($"New kill report from {player}.");

        var query = EntityQueryEnumerator<MistGameRuleComponent>();
        while (query.MoveNext(out var _, out var rule))
        {
            rule.AlivePlayers.Remove(player);

            var lifetime = EnsureComp<LifeTrackerComponent>(player);

            if (!lifetime.IsDead)
            {
                _sawmill.Error("Life tracker component didn't catch KillReport before game rule!");
                lifetime.IsDead = true;
                lifetime.DeathTime = _timing.CurTime;
            }

            var characterName = _mind.TryGetMind(player, out var mindId, out _) ? rule.Minds.Find(e => e.Item1 == mindId).Item2 : "[lost name]";

            rule.PlayersGrave.Add(GenerateGraveMessage(ev, lifetime, characterName));

            if (rule.AlivePlayers.Count != 0)
                continue;

            // end
            _sawmill.Info("Everyone dead");
            var roundEnd = EntityManager.EntitySysManager.GetEntitySystem<RoundEndSystem>();
            roundEnd.EndRound(TimeSpan.FromSeconds(45));
        }
    }

    private void OnRuleStartup(Entity<MistGameRuleComponent> ent, ref ComponentStartup args)
    {
        EntityManager.System<ArrivalsSystem>().SetArrivals(false); // ensure that arrivals disabled

        RaiseLocalEvent(new EnableQueueSpawningEvent(ent.Comp.PlayerMob, ent.Comp.PlayerSpawner));
        RaiseLocalEvent(new PauseWavesEvent());
    }

    private void OnAfterPlayerSpawner(PlayerSpawnAfterEvent ev)
    {
        var query = EntityQueryEnumerator<MistGameRuleComponent>();
        while (query.MoveNext(out var _, out var rule))
        {
            _sawmill.Debug("New player was spawned! Start initializing...");

            if (_mind.TryGetMind(ev.PlayerEntity, out var mindId, out var _))
            {
                var characterName = MetaData(ev.PlayerEntity).EntityName;
                rule.Minds.Add((mindId, characterName));
            }

            var mistPlayer = EnsureComp<MistPlayerComponent>(ev.PlayerEntity);
            var killTracker = EnsureComp<KillTrackerComponent>(ev.PlayerEntity);
            EnsureComp<LifeTrackerComponent>(ev.PlayerEntity);

            killTracker.RegisterOnlyDeath = true;

            rule.AlivePlayers.Add(ev.PlayerEntity);

            _sawmill.Debug("New player was successfully initialized!");

            if (rule.Started)
                continue;

            _sawmill.Debug("Mist Game haven't started yet, initialize everything.");

            SpawnSupply(rule.SupplyTiming,
                rule.SmoothingSupplyTiming,
                rule.SupplyCategoryName,
                rule.SupplyAttention,
                rule.SupplyTimerToken);
            RaiseLocalEvent(new UnpauseWavesEvent());

            rule.Started = true;
        }
    }

    private void OnWaveStart(WaveStartsEvent ev)
    {
        // Ensure that mist game rule exists
        var query = EntityQueryEnumerator<MistGameRuleComponent>();
        while (query.MoveNext(out _, out _))
        {
            RaiseLocalEvent(new DisableQueueSpawningEvent());
        }
    }

    private void OnWaveEnemiesDead(AllWaveEnemiesDead _)
    {
        var query = EntityQueryEnumerator<MistGameRuleComponent>();
        while (query.MoveNext(out var _, out var rule))
        {
            RaiseLocalEvent(new EnableQueueSpawningEvent("MobHuman", rule.PlayerSpawner));
            RaiseLocalEvent(new MultiplyDifficultyEvent(rule.Escalation));
        }
    }

    private string GenerateGraveMessage(KillReportedEvent ev, LifeTrackerComponent lifeTracker, string characterName)
    {
        // TODO: move to localization
        var primary =
            $"{characterName} - from {lifeTracker.StartupTime} to {lifeTracker.DeathTime} / {lifeTracker.Lifetime}";

        if (ev.Primary is KillEnvironmentSource)
        {
            primary = $"{primary}\n\tYou couldn't stand the environment";
        }
        else if (ev.Primary is KillPlayerSource playerSource)
        {
            if (ev.Suicide)
            {
                primary = $"{primary}\n\tWe remember you, the mate...";
            }
            else
            {
                primary = $"{primary}\n\tMade a mistake with the choice of a friend.";
                if (_mind.TryGetMind(playerSource.PlayerId, out var mind))
                {
                    primary = $"{primary}\n\tTHANK YOU, THE \"ALLY\" {mind.Value.Comp.CharacterName ?? "murderer"}!";
                }
            }
        }
        else if (ev.Primary is KillNpcSource npcSource)
        {
            primary = $"{primary}\n\tFound your death in fight with {MetaData(npcSource.NpcEnt).EntityName}.";
        }
        else
        {
            primary = $"{primary}\n\tNobody knows, how them dead. Developer too.";
        }

        return primary;
    }

    private void OnObjectivesTextGetInfo(Entity<MistGameRuleComponent> ent, ref ObjectivesTextGetInfoEvent args)
    {
        args.Minds = ent.Comp.Minds;
        args.AgentName = "survivor";
    }

    private void OnObjectivesTextPrepend(Entity<MistGameRuleComponent> ent, ref ObjectivesTextPrependEvent args)
    {
        foreach (var grave in ent.Comp.PlayersGrave)
        {
            args.Text += grave + "\n";
        }
    }

    private void SpawnSupply(TimeSpan timing,
        TimeSpan smooth,
        string supplyCategory,
        SoundSpecifier supplyAttention,
        CancellationTokenSource token)
    {
        _audio.PlayGlobal(supplyAttention, Filter.Broadcast(), true);
        Timer.Spawn(TimeSpan.FromSeconds(12), () => RaiseLocalEvent(new SpawnSupplyEvent(supplyCategory)), token.Token);
        Timer.Spawn(timing + smooth * _random.NextFloat(-1, 1),
            () => { SpawnSupply(timing, smooth, supplyCategory, supplyAttention, token); },
            token.Token);
    }

    private void OnRoundEnd(RoundEndSystemChangedEvent ev)
    {
        var query = EntityQueryEnumerator<MistGameRuleComponent>();
        while (query.MoveNext(out var _, out var rule))
        {
            rule.SupplyTimerToken.Cancel();
        }
    }
}
