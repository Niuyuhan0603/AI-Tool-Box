# C# 项目开发规则（AI 可执行版）

> 本规则文档旨在确保 AI（如 GitHub Copilot、Trae）生成的 C# 代码始终符合严谨、现代、安全、高性能和可维护的标准。任何违反规则的代码都应被拒绝并要求重写。

---

## 1. 硬性规则（必须100%遵守）

- ✅ **语言与框架**：仅使用 **C#** 和 **Windows 原生 UI 框架**（WinForms / WPF / WinUI 3）。禁止使用 Web 技术（如 Blazor Hybrid、WebView2）作为主 UI，除非经项目负责人明确批准。
- ✅ **语法严谨性**：禁止使用不安全的语法（如 `unsafe` 代码块、指针），除非处理特定底层需求且经过严格测试。
- ✅ **弃用包**：禁止引用任何官方已标记为“弃用”或“过时”的 NuGet 包。在添加依赖前，必须检查其最新稳定版本和生命周期。
- ✅ **异常处理**：所有可能抛出异常的代码（网络请求、文件 I/O、数据库操作）必须包裹在 `try-catch` 中，至少记录异常信息，并恢复或通知用户。
- ✅ **异步模式**：所有 I/O 操作和长时间运算必须使用 `async/await`，避免阻塞 UI 线程。禁止使用 `.Result` 或 `.Wait()` 进行同步等待。
- ✅ **资源释放**：任何 `IDisposable` 对象（如 `HttpClient`、`FileStream`）必须使用 `using` 语句或显式调用 `Dispose()`。

---

## 2. 编码规范

### 命名约定
| 元素 | 规范 | 示例 |
|------|------|------|
| 类/接口/记录 | PascalCase | `UserService`, `IUserRepository` |
| 方法/属性/事件 | PascalCase | `GetUserById()`, `UserName` |
| 参数/局部变量 | camelCase | `userId`, `userName` |
| 常量 | PascalCase 或 UPPER_SNAKE_CASE | `MaxRetryCount` |
| 私有字段 | `_camelCase` (以下划线开头) | `_logger`, `_settings` |

### 代码格式
- 缩进：4 个空格（**禁用 Tab**）
- 花括号：K&R 风格（左大括号不换行，右大括号换行）
- 每行最多 120 字符（但可适当放宽，便于阅读）
- 使用 `readonly` 修饰不可变字段
- 使用 `var` 仅在类型明显时（如 `var list = new List<int>()`），否则显式声明

### 注释与文档
- 所有公共类、方法、属性必须包含 XML 文档注释（`///`），描述其目的、参数、返回值和异常。
- 复杂逻辑必须添加内联注释解释“为什么”这样做，而非“做什么”（后者由代码本身表达）。

---

## 3. 架构原则

- **分层架构**：UI 层（View） → 业务逻辑层（ViewModel/Service） → 数据访问层（Repository/DAO）。UI 层禁止直接操作数据库或文件系统。
- **依赖注入**：使用 `Microsoft.Extensions.DependencyInjection` 进行依赖注入，便于单元测试和替换实现。
- **单一职责**：每个类只负责一个功能模块，避免“上帝类”。
- **接口隔离**：使用接口定义契约，便于 Mock 和扩展。
- **事件驱动**：UI 交互通过事件或命令（`ICommand`）触发，业务逻辑通过消息/事件总线（如 `Prism` 或 `MVVM Toolkit`）通信。

---

## 4. UI/UX 与动画

- **UI 框架选择**（按优先顺序）：
  1. **WinUI 3**（推荐，最现代化，支持 Windows 11 云母/亚克力效果）
  2. **WPF**（成熟稳定，资源丰富，可配合 `MaterialDesignInXamlToolkit` 实现 Material Design）
  3. **WinForms**（仅用于极简工具或维护旧项目）
- **动画库选择**（按优先顺序）：
  1. **WinUI 3 内置动画**（Composition Animations、Storyboard）
  2. **Lottie** 为 WPF/Avalonia 播放 JSON 动画
  3. **FluentTransitions** 或 **XamlFlair** 实现通用过渡动画
- **视觉效果**：若需“液态玻璃”（Liquid Glass）效果，使用 **Win2D** 在 WinUI 3 中实现，或使用 **LiquidGlassAvaloniaUI**（若选择 Avalonia）。
- **响应式布局**：界面必须适配不同窗口大小，使用 `Grid`、`StackPanel`、`RelativePanel` 等布局容器，避免硬编码尺寸。

---

## 5. 性能与资源管理

- **异步加载**：所有数据获取、图片加载、网络请求必须在后台线程执行，并通过 `Progress<T>` 或 `IProgress<T>` 更新 UI。
- **缓存策略**：频繁使用的数据（如配置、常用查询结果）应缓存到内存或本地存储（如 `SQLite`），避免重复 I/O。
- **避免内存泄漏**：
  - 在 WPF 中，及时解除事件订阅（使用弱事件模式或 `Unsubscribe`）。
  - 在 WinUI 3 中，正确使用 `x:Bind` 和 `OneWay` 绑定，避免强引用。
- **启动性能**：主窗口应快速加载，耗时初始化（如加载模型）显示加载提示，并异步执行。

---

## 6. 测试与质量保证

- **单元测试**：所有业务逻辑层必须包含单元测试（使用 `xUnit` 或 `NUnit`），覆盖边界条件和异常路径。
- **UI 测试**：关键用户流程（如登录、提交表单）应包含 UI 自动化测试（使用 `WinAppDriver` 或 `Appium`）。
- **代码分析**：启用 `.NET` 代码分析器（如 `Microsoft.CodeAnalysis.NetAnalyzers`），确保无警告。
- **日志**：使用 `Serilog` 或 `NLog` 记录应用程序日志，包含时间戳、级别、类别和详细信息。

---

## 7. 安全准则

- **输入验证**：所有用户输入（文本框、文件路径）必须进行验证，防止注入或路径遍历攻击。
- **敏感数据**：密码、API 密钥等必须加密存储（使用 `DataProtection` API 或 `Windows.CredentialManager`），绝不以明文保存在配置文件或代码中。
- **HTTPS**：所有外部 API 调用必须使用 HTTPS，并验证证书有效性。

---

## 8. 依赖管理

- **NuGet 包**：所有依赖必须明确列在 `.csproj` 中，并指定版本号（使用 `PackageReference`）。
- **版本策略**：优先使用 LTS 版本（如 .NET 8/6），避免使用预览版或 nightly 版。
- **冲突解决**：若出现版本冲突，优先升级兼容版本，而非降级或忽略。

---

## 9. 文档与提交规范

- **README**：每个项目必须包含 `README.md`，说明项目目的、快速启动、依赖和配置说明。
- **提交信息**：使用语义化提交信息（如 `feat: 添加用户登录`，`fix: 修复加载超时问题`）。
- **变更日志**：记录每个版本的主要变化（`CHANGELOG.md`）。

---

## 10. AI 协作规则

- **理解与澄清**：在生成代码前，AI 必须重述需求，提出澄清问题（如不明确的地方），确保理解无误。
- **代码审查**：生成的代码必须遵循上述所有规则，AI 应在代码块末尾附加“合规声明”，说明已检查的规则。
- **迭代改进**：如果用户反馈代码不符合规则，AI 必须根据反馈修正，并解释修改点。

---

## 附录：推荐的 NuGet 包列表（根据用途）

| 用途 | 推荐包 |
|------|--------|
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` |
| JSON 序列化 | `System.Text.Json`（首选）或 `Newtonsoft.Json` |
| HTTP 客户端 | `Microsoft.Extensions.Http` |
| 日志 | `Serilog` 或 `NLog` |
| MVVM 框架 | `CommunityToolkit.MVVM`（轻量）或 `Prism`（重量） |
| UI 控件库 | `MaterialDesignInXamlToolkit` (WPF) |
| 动画 | `XamlFlair` (WPF), `Lottie-WPF` |
| 数据库 | `Dapper`（轻量）或 `Entity Framework Core`（重量） |
| 单元测试 | `xUnit` + `Moq` |
| 异步流 | `System.Threading.Channels` |

---

**本规则文档应由项目负责人审核并维护，如有变更须同步更新。AI 在每次代码提交前应自检是否符合以上规则。**