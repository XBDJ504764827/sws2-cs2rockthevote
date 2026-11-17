# SWS2 CS2 Rock The Vote

为 SwiftlyS2 服务器提供地图预定和玩家投票换图。`!yd` 预定指定地图；`!rtv` 同意换图。没有预定时，达到 RTV 门槛会打开选图 HUD，由玩家票选下一张地图。

## 与其他 SWS2 插件搭配

| 插件 | 职责 | 与本插件的关系 |
| --- | --- | --- |
| [sws2-cs2menus](https://github.com/XBDJ504764827/sws2-cs2menus) | 共享菜单、分页、导航、地图选择 HUD | 预定和选图面板所需的基础服务 |
| [sws2-cs2mapmanager](https://github.com/XBDJ504764827/sws2-cs2mapmanager) | 统一地图目录、评分、工坊下载和可取消换图 | 地图目录和换图所需的基础服务 |
| [sws2-cs2admin](https://github.com/XBDJ504764827/sws2-cs2admin) | 管理员权限、玩家管理、封禁、换图控制 | 可选；管理权限与取消/暂停换图入口 |
| [sws2-cs2rockthevote](https://github.com/XBDJ504764827/sws2-cs2rockthevote) | 玩家预定地图、RTV 发起投票与选图 | 本插件 |
| [sws2-flashlight](https://github.com/XBDJ504764827/sws2-flashlight) | 随视角移动的手电筒 | 可选，独立照明功能 |
| [sws2-nightvision](https://github.com/XBDJ504764827/sws2-nightvision) | 玩家独立夜视与亮度偏好 | 可选，独立夜视功能 |

常用组合：玩家换图安装 **menus + mapmanager + rockthevote**；需要管理面板时再加 **admin**；手电筒和夜视按需加入。工坊地图自动下载和 HUD 工坊资源自动挂载还需要服务器安装 **MultiAddonManager**。本套件不提供 KZ 计时、检查点或 CKZ/VNL 模式本身。

## 安装与准备

需要 SwiftlyS2 **1.5.0 或更新版本**、menus 和 mapmanager。admin 可选，不影响普通玩家投票；工坊地图自动下载需要 MultiAddonManager。

1. 下载配套插件包，完整解压到 `csgo/addons/swiftlys2/plugins/`，保留合约 DLL、`resources/exports/`、模板和翻译。
2. 手动加载先运行 `sw plugins load sws2-cs2menus`、`sw plugins load sws2-cs2mapmanager`，再运行 `sw plugins load sws2-cs2rockthevote`；整套安装后也可重启服务器。
3. 在 mapmanager 的 `config.jsonc` 配置 `Maps.Available`，这里不维护第二份地图列表。
4. 在 menus 配置并挂载 HUD addon；先用 `!menudemo` 验证。没有 HUD 资源时，中心 HTML 回退菜单仍可提供预定和选图。
5. 玩家进入游戏输入 `!yd` 或 `!rtv`。这些命令仅供真人玩家使用，不要求管理员权限。

## 命令

聊天前缀以服务器配置为准，本文使用 `!`。这些命令也注册了无前缀的玩家控制台形式。

| 聊天命令 | 玩家控制台 | 用法 |
| --- | --- | --- |
| `!yd` | `yd` | 打开预定面板，选择一张地图 |
| `!yd <地图名称或ID>` | `yd <地图名称或ID>` | 按显示名称、稳定 ID 或地图值精确匹配预定，忽略大小写 |
| `!rtv` | `rtv` | 同意已预定地图，或推动无人预定的选图投票；活跃投票中可重开未提交的选票 |

## 已预定地图：同意后换图

1. 玩家输入 `!yd`，地图面板立即打开。每页四张地图，显示截图、名称和四类难度；Steam 作者/标题信息有缓存时一并显示。
2. 选择地图后面板关闭，聊天宣布预定。也可直接用 `!yd kz_func_detail`。
3. 其他玩家及预定者输入 `!rtv`，达到下面的人数门槛后，mapmanager 开始 `ChangeDelaySeconds` 倒计时。
4. 到期切换已安装地图；目标工坊地图缺失则开始下载，下载完成后自动切换。

后续预定会替换之前预定，并清空旧 RTV 同意票。选图投票或换图已经进行中时，不接受用预定覆盖当前流程。

## 无人预定：打开 HUD 选择地图

1. 玩家输入 `!rtv`，先累计请求票，不按地图列表顺序自动选下一张。
2. 达到门槛后，为投票开始时在线的所有真人玩家打开选图面板，排除当前地图。面板样式、截图、评分和翻页与 `!yd` 一致。
3. 每人只能选一张；所有人选完立即统计，最高票胜出，最高票并列时在并列地图中等概率随机。
4. 胜出后以 **0 秒倒计时**交给 mapmanager 发起换图，不使用预定模式的 5 秒等待。缺失工坊地图仍需等待下载完成。

| 在线真人数 | 发起选图所需 `!rtv` 票数 | 选图示例 |
| --- | --- | --- |
| 1 | 1 | 输入后立即打开面板，选完立即发起换图 |
| 2 | 2 | 两人都输入后打开面板；不同地图各一票随机二选一 |
| 3 | 2 | 三人都会收到面板；各选一张随机三选一，2 比 1 选择两票地图 |
| 4 或更多 | `ceil(人数 × 0.8)` | 最高票地图胜出，最高票并列随机 |

同样的 RTV 门槛也适用于已预定地图。例如 4 人需要 4 票，5 人需要 4 票。机器人不计入人数；未输入 `!rtv` 但在选图开始时在线的真人仍能提交地图选择。

### 超时、关窗、退出和中途加入

- 选图默认最长 **30 秒**，配置 `MapVoteDurationSeconds` 限制为 5–120 秒。全部选完会提前结束，不强制等待倒计时。
- 超时按已经提交的票统计；无人选择则取消，不按目录顺序补选。
- 关闭或被其他菜单替换后，未提交的玩家可输入 `!rtv` 重开；已提交的选择不能重复或修改。
- 中途加入者等下一次投票。断线移除该玩家及其票，剩余玩家都已选完时可立即结束。
- 地图卸载、插件关闭/卸载、管理员取消/暂停，以及依赖服务变化会清理活跃投票；旧面板回调不能投到新一轮。

## 配置

首次加载生成 `csgo/addons/swiftlys2/configs/plugins/sws2-cs2rockthevote/config.jsonc`，支持热重载。

```jsonc
{
  "sws2-cs2rockthevote": {
    "Enabled": true,
    "ChangeDelaySeconds": 5,
    "MapVoteDurationSeconds": 30,
    "WorkshopMetadata": {
      "Enabled": true,
      "TimeoutSeconds": 30
    }
  }
}
```

| 设置 | 默认值 | 作用 |
| --- | --- | --- |
| `Enabled` | `true` | 预定及投票总开关；关闭会清理投票并取消 RTV 自己的待处理换图 |
| `ChangeDelaySeconds` | `5` | 已预定地图达到门槛后的倒计时，限制 5–30 秒 |
| `MapVoteDurationSeconds` | `30` | 无预定选图最多等待秒数，限制 5–120 秒 |
| `WorkshopMetadata.Enabled` | `true` | 可选的 Steam 标题/作者/预览信息查询 |
| `WorkshopMetadata.TimeoutSeconds` | `30` | 单个 HTTP 请求超时，限制 1–120 秒 |

旧配置不含 `MapVoteDurationSeconds` 时使用 30 秒默认值，不需要删除重建。元数据设置热重载后影响后续请求，已发出的请求可能持续到原超时。

## 管理员如何控制 RTV

安装 admin 并授予 `sws2.admin.map.control` 后，可从 `!admin` → 地图管理操作，或使用：

| 命令 | 服务器控制台 | 结果 |
| --- | --- | --- |
| `!cancelmap` | `sw_cancelmap` | 清空同意票、关闭活跃选图，取消倒计时或下载完成后的换图；允许重新投票 |
| `!pausemaps` | `sw_pausemaps` | 取消当前流程并阻止新托管换图 |
| `!resumemaps` | `sw_resumemaps` | 恢复接受新投票，不复活旧票；预定地图保留 |

暂停在地图卸载/重启后重置。底层 Steam 下载可能在取消后继续；已发给引擎的换图命令不能撤回。管理员直接选图由 mapmanager 统一管理，开始该流程也会取消活跃的玩家选图。

## 地图目录、评分、截图与 Steam 数据

地图只在 [mapmanager](https://github.com/XBDJ504764827/sws2-cs2mapmanager) 配置；admin 与 RTV 共用。四类评分分别是 `CKZ.NUB` / `CKZ.PRO` / `VNL.NUB` / `VNL.PRO`，NUB 为存点、PRO 为裸跳，未知填 `null`。改评分后重开面板即可。

面板不等待 Steam HTTP 请求：先用配置和缓存打开，再后台刷新。Steam 无法访问时回退到已有信息，输出简短日志并等待五分钟后的后续请求重试，不影响选图和 mapmanager 下载。可在网络不稳定时关闭 `WorkshopMetadata.Enabled`。

HUD 截图来自 menus addon 中打包的本地 CSS 背景，Steam 元数据刷新不会自动替换截图。为自己的目录生成截图，需在 menus 仓库运行：

```sh
python3 tools/build_map_thumbnails.py --config /path/to/mapmanager/config.jsonc
```

随后用 Workshop Tools 编译 XML、CSS、图片，更新 HUD addon。离线映射、缓存和资源挂载详见 [menus Workshop 指南](https://github.com/XBDJ504764827/sws2-cs2menus/blob/develop/workshop/README.md)。缺图显示占位，不阻止预定/投票。HUD addon ID 在 menus 配置，与地图 ID 分开。

## 常见问题

| 现象 | 检查 |
| --- | --- |
| `!yd` 提示地图服务不可用 | mapmanager 加载日志和配套合约 |
| 提示菜单不可用或只有中心 HTML | menus 加载情况、HUD addon、服务器 `sw_hudstatus` |
| 单人 RTV 没立即打开面板 | 是否已有预定、已暂停、已有换图、目录只有当前地图或没有可切地图 |
| 两人只输入一次 RTV 没面板 | 需要两名玩家分别同意，重复输入不增加票 |
| 三人两票打开后还在等待 | 所有三名玩家都可选图，等待第三人选择或选图期限 |
| 换图结果产生但尚未切换 | 工坊地图可能尚在下载；检查 MAM、Steam 和换图日志 |
| 仍按目录顺序自动换图 | 确认实际加载的是更新后的 rockthevote 包 |

## 源码构建与验证

需要 .NET 10 SDK；本地将 menus、mapmanager 与本仓库放在同级目录，或使用项目支持的仓库内依赖路径。

```sh
dotnet restore sws2-cs2rockthevote.csproj
dotnet build sws2-cs2rockthevote.csproj -c Release -warnaserror
dotnet publish sws2-cs2rockthevote.csproj -c Release --no-restore
dotnet run --project tests/VoteChecks/VoteChecks.csproj -c Release
dotnet run --project tests/MetadataChecks/MetadataChecks.csproj -c Release
```

产物为 `build/sws2-cs2rockthevote.zip`。投票检查覆盖单人、两/三人平票、多数结果、重复/无效选择、部分投票和断线；元数据检查覆盖嵌套超时、缓存、关闭查询和取消。

## 升级与发布

与 menus、mapmanager、admin 使用兼容的共享合约，升级整套后重启服务器。保存现有配置和菜单 HUD ID；仅修改 RTV 投票逻辑不需要重新发布 HUD addon。

仓库使用 `develop` → `main`：PR / develop 构建测试包，PR 可发布预览版本，合并 main 自动计算版本、打包并创建 Release。`src/VersionInfo.cs` 是本地回退值，正式版本由发布流程注入。
