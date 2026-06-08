using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>控制室主仪表盘屏幕，展示系统概览并分发用户导航操作。</summary>
public static class DashboardScreen
{
    public static async Task ShowAsync(
        ControlRoomService service,
        bool autoRefresh = false,
        int refreshSeconds = 2,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await service.GetDashboardAsync(cancellationToken).ConfigureAwait(false);
        DashboardRenderer.Render(snapshot, autoRefresh, refreshSeconds);
    }
}
