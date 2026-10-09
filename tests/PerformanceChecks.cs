using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IdleSword.Core;
using IdleSword.Features;

/// <summary>可重复的效率评估；计时不作为机器相关的通过阈值，轨迹摘要用于同配置下核对优化前后行为。</summary>
internal static class PerformanceChecks
{
    private const int Steps = 2400;

    public static void Run(GameConfig config)
    {
        Console.WriteLine("PERF .NET=" + Environment.Version + " steps=" + Steps + " samples=5 median");
        foreach (int count in new[] { 0, 16, 96 })
        {
            string name = count == 0 ? "level-loop" : "sandbox-" + count;
            Measure(name, () =>
            {
                var session = Create(config, count);
                return () => { for (int i = 0; i < Steps; i++) session.Step(.05); };
            });
            // 与计时隔离：每拍序列化状态、效果、伤害账与自身增益，能发现同一终态下的中途顺序差异。
            var traced = Create(config, count);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < Steps; i++)
            {
                traced.Step(.05);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    traced.State, traced.Effects, traced.DamageStats.Rows,
                    traced.Moving, traced.Elapsed, traced.BuffRemaining, traced.BuffPower,
                    traced.ShieldRemaining, traced.ShieldAmount, traced.HasteRemaining,
                    traced.CritBonusRemaining, traced.MirrorRemaining,
                }));
            }
            Console.WriteLine("TRACE " + name + " " + Convert.ToHexString(hash.GetHashAndReset()));
        }
        Measure("attribute-reads", () =>
        {
            var session = Create(config, 0);
            return () =>
            {
                double total = 0;
                for (int i = 0; i < 10000; i++) total += session.Attack + session.MaxHp + session.AttackRange;
                GC.KeepAlive(total);
            };
        });
        Measure("save-clone", () =>
        {
            var session = Create(config, 96);
            return () => { for (int i = 0; i < 25; i++) GC.KeepAlive(SaveStore.Clone(session.State)); };
        });
    }

    private static GameSession Create(GameConfig config, int count)
    {
        var session = new GameSession(config, seed: 42)
        {
            PlayerInvincible = true, SandboxMode = count > 0,
        };
        session.State.Talents["t_auto"] = 1;
        session.State.Talents["t_ranged"] = 1;
        foreach (var skill in config.Skills.Values) session.State.Skills[skill.Id] = 8;
        if (count == 0) return session;
        var monster = config.Monsters.Values.First(m => m.Kind == "normal");
        for (int i = 0; i < count; i++)
            session.Battle.Enemies.Add(new EnemyState
            {
                Id = session.Battle.NextEnemyId++, MonsterId = monster.Id,
                Hp = 1e9, MaxHp = 1e9, X = session.Battle.PlayerX + 80 + i % 8 * 95,
                Atk = monster.Atk, StunUntil = 1e6,
            });
        return session;
    }

    private static void Measure(string name, Func<Action> prepare)
    {
        prepare()(); // 预热 JIT 与静态初始化，准备会话不计入 Step 热路径。
        var times = new List<double>();
        var allocations = new List<long>();
        for (int sample = 0; sample < 5; sample++)
        {
            var run = prepare();
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            run();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        }
        times.Sort(); allocations.Sort();
        Console.WriteLine(FormattableString.Invariant($"PERF {name} ms={times[2]:F3} bytes={allocations[2]}"));
    }
}
