namespace FavSync.Server;

/// <summary>
/// 让"运行中的服务"能被宿主重启。改总库地址/端口必须重启服务才生效（数据库路径在启动时定死），
/// 而重启这件事只有进程的宿主会说：托盘壳能做到，命令行 <c>mfav.exe</c> 做不到。
/// </summary>
/// <remarks>
/// 用接口而不是直接引用桌面壳：FavSync.Server 不能反过来依赖 FavSync.Desktop，
/// 否则命令行版就打不进同一个程序集了。桌面壳在 <c>CreateApp(..., configure)</c> 里注册实现。
/// </remarks>
public interface IServerControl
{
    /// <summary>宿主是否真的有能力重启（注册了实现且当前能停能起）。</summary>
    bool CanRestart { get; }

    /// <summary>
    /// 请求重启。<b>必须在响应写回浏览器之后再动手</b>，否则调用方拿到的是被掐断的连接。
    /// 返回 false 表示宿主拒绝了这次重启。
    /// </summary>
    bool RequestRestart();
}
