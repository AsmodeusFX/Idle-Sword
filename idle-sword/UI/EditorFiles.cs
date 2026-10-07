namespace IdleSword.UI;

/// <summary>
/// 开发工具（节点编辑器 / 关卡编辑器）写配置表的公共部分。**只有一件事**：怎么把几张表一起写下去而不留半成品。
/// 内容怎么组织、哪些格子归谁，那个各编辑器自己管——它们的表形状差得远（一个是图 + 侧车，一个是三张平表）。
/// </summary>
public static class EditorFiles
{
    /// <summary>
    /// 多文件一起保存：任一失败，就把已经写过的按快照回滚。
    /// 单文件的 <see cref="WriteAtomic"/> 保证不了跨文件一致，而"两边 id 对不上"是加载期
    /// **直接拒绝整份配置**的错——不能让半成品落在磁盘上。备份允许文件不存在（侧车第一次跑时就是这种情形）。
    /// </summary>
    public static void WriteAllOrNothing(params (string Path, string Text)[] files)
    {
        var backup = files.Select(f => (f.Path, Old: File.Exists(f.Path) ? File.ReadAllText(f.Path) : null)).ToList();
        try { foreach (var (path, text) in files) WriteAtomic(path, text); }
        catch
        {
            foreach (var (path, old) in backup)
            {
                try
                {
                    if (old is null) { if (File.Exists(path)) File.Delete(path); }
                    else WriteAtomic(path, old);
                }
                catch { /* 回滚本身失败就只能留下原始异常，别再盖掉它 */ }
            }
            throw;
        }
    }

    /// <summary>先写 `.tmp` 再原子替换——中途失败不会把源表写坏。</summary>
    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
