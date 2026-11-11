# 使用 Supabase 构建 C# 客户端数据验证服务（AI 可直接执行版）

> 本方案适用于 **TraeCode、GitHub Copilot Chat、Cursor** 等 AI 编程助手，可直接根据此文档生成 C# 客户端代码。

---

## 📌 项目目标
- 使用 **Supabase 免费计划** 作为后端数据库（PostgreSQL）
- C# 客户端（WinUI/WPF/WinForms）通过 Supabase API 实现：
  - 用户登录（用户名/密码验证）
  - 邀请码验证（检查邀请码是否有效）
  - 内部测试码验证（类似许可证）
- 数据量极小（< 500 MB），延迟容忍度 ≤ 3000ms
- 无需部署任何服务器代码，完全依赖 Supabase 提供的 REST API

---

## 🧩 整体架构

```
C# 客户端 --(HTTPS)--> Supabase REST API --> PostgreSQL 数据库
```

所有逻辑在客户端完成，Supabase 负责数据存储和简单查询。

---

## ✅ 第一阶段：Supabase 项目准备

### 1. 注册 Supabase 账号
- 访问 [Supabase 官网](https://supabase.com)
- 点击 “Start your project” 或 “Sign in”
- 使用 GitHub 账号或邮箱注册（免费，无需信用卡）

### 2. 创建新项目
- 登录后点击 “New project”
- 填写项目名称（如 `myapp-auth`）
- 设置数据库密码（**务必记住**）
- 选择区域：**推荐新加坡（Southeast Asia）**，延迟相对较低
- 点击 “Create new project”，等待约 2-3 分钟初始化

### 3. 获取 API 连接信息
项目创建完成后，在 Dashboard 中：
- 点击左侧菜单 **“Settings”** → **“API”**
- 记录以下信息：
  - **Project URL**（形如 `https://xxxxx.supabase.co`）
  - **anon public** 密钥（形如 `eyJhbGciOiJIUzI1NiIs...`）
  - **Service Role Secret**（仅用于管理，客户端开发一般用 anon key）

> ⚠️ 客户端只能使用 `anon` 密钥，**绝对不能**暴露 `service_role` 密钥。

### 4. 配置数据库表（通过 SQL 编辑器）
在 Supabase Dashboard 中，点击左侧 **“SQL Editor”**，新建一个查询，执行以下 SQL：

```sql
-- 用户表
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,   -- 使用哈希存储，推荐 bcrypt
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 邀请码表
CREATE TABLE invite_codes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code TEXT UNIQUE NOT NULL,
    used BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 测试码表（内部使用）
CREATE TABLE test_codes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code TEXT UNIQUE NOT NULL,
    valid_until TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 插入示例数据（可选）
INSERT INTO invite_codes (code) VALUES ('INVITE123'), ('INVITE456');
INSERT INTO test_codes (code, valid_until) VALUES ('TEST001', NOW() + INTERVAL '30 days');
```

> 实际生产环境，密码应使用 `bcrypt` 或 `scrypt` 存储哈希。为简化演示，我们使用 `pgcrypto` 扩展，后续在 C# 中生成哈希再存储。

### 5. 启用 Row Level Security (RLS) 并设置策略
Supabase 默认开启 RLS，需要设置允许读取和插入的策略：

在 SQL Editor 中执行：

```sql
-- 启用 RLS（默认已开启）
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE invite_codes ENABLE ROW LEVEL SECURITY;
ALTER TABLE test_codes ENABLE ROW LEVEL SECURITY;

-- 允许匿名用户查询邀请码（只读）
CREATE POLICY "Allow select invite codes" ON invite_codes
    FOR SELECT USING (true);

-- 允许匿名用户查询测试码（只读）
CREATE POLICY "Allow select test codes" ON test_codes
    FOR SELECT USING (true);

-- 用户表：仅允许插入（注册），不允许查询其他用户（隐私）
CREATE POLICY "Allow insert users" ON users
    FOR INSERT WITH CHECK (true);
```

> ⚠️ 注意：这里为了演示，对 `invite_codes` 和 `test_codes` 开放了匿名查询。实际项目可增加更多限制（如仅允许指定 token）。

---

## 💻 第二阶段：C# 客户端集成 Supabase

### 2.1 安装 NuGet 包
在 C# 项目中安装 Supabase 官方 SDK（支持 .NET Standard 2.0+）：

```bash
dotnet add package Supabase
dotnet add package Supabase.Gotrue  # 用于用户认证（可选）
```

### 2.2 创建 Supabase 客户端单例
新建 `SupabaseService.cs`：

```csharp
using Supabase;

public static class SupabaseService
{
    private static Client _instance;

    public static Client Instance
    {
        get
        {
            if (_instance == null)
            {
                var options = new SupabaseOptions
                {
                    AutoConnectRealtime = false   // 本项目不需要实时功能
                };
                _instance = new Client(
                    "https://你的项目ID.supabase.co",   // 替换为你的 Project URL
                    "你的anon密钥",                     // 替换为你的 anon key
                    options
                );
                _instance.InitializeAsync().Wait();  // 异步初始化
            }
            return _instance;
        }
    }
}
```

### 2.3 数据模型定义
创建 `Models/User.cs`：

```csharp
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

[Table("users")]
public class User : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("username")]
    public string Username { get; set; }

    [Column("password_hash")]
    public string PasswordHash { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
```

创建 `Models/InviteCode.cs`：

```csharp
[Table("invite_codes")]
public class InviteCode : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("code")]
    public string Code { get; set; }

    [Column("used")]
    public bool Used { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
```

创建 `Models/TestCode.cs`：

```csharp
[Table("test_codes")]
public class TestCode : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("code")]
    public string Code { get; set; }

    [Column("valid_until")]
    public DateTime? ValidUntil { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
```

### 2.4 业务逻辑方法（SupabaseService 扩展）

在 `SupabaseService` 中添加静态方法：

```csharp
using Supabase.Postgrest;
using System.Security.Cryptography;
using System.Text;

public static class SupabaseService
{
    // ... 之前的 Instance 代码 ...

    public static async Task<bool> ValidateInviteCodeAsync(string code)
    {
        var client = Instance;
        var result = await client.From<InviteCode>()
            .Where(x => x.Code == code && x.Used == false)
            .Get();
        return result.Models.Any();
    }

    public static async Task<bool> ValidateTestCodeAsync(string code)
    {
        var client = Instance;
        var now = DateTime.UtcNow;
        var result = await client.From<TestCode>()
            .Where(x => x.Code == code && x.ValidUntil > now)
            .Get();
        return result.Models.Any();
    }

    public static async Task<bool> RegisterUserAsync(string username, string password)
    {
        var client = Instance;
        // 注意：密码应哈希存储，这里使用 SHA256 示例（生产环境请用 bcrypt）
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        var passwordHash = Convert.ToBase64String(hashedBytes);

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHash,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var result = await client.From<User>().Insert(newUser);
            return result.Models.Count > 0;
        }
        catch
        {
            // 处理重复用户名等错误
            return false;
        }
    }

    public static async Task<bool> LoginAsync(string username, string password)
    {
        var client = Instance;
        // 查询用户
        var result = await client.From<User>()
            .Where(x => x.Username == username)
            .Get();

        var user = result.Models.FirstOrDefault();
        if (user == null) return false;

        // 验证密码哈希
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        var inputHash = Convert.ToBase64String(hashedBytes);

        return user.PasswordHash == inputHash;
    }
}
```

> 🔐 安全提示：以上使用 SHA256 仅作演示，生产环境强烈建议使用 `BCrypt.Net-Next` 等库。

---

## 🖥️ 第三阶段：在 UI 中调用

### WinForms 示例

```csharp
private async void LoginButton_Click(object sender, EventArgs e)
{
    var username = txtUsername.Text;
    var password = txtPassword.Text;

    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
    {
        MessageBox.Show("请输入用户名和密码");
        return;
    }

    var success = await SupabaseService.LoginAsync(username, password);
    if (success)
    {
        MessageBox.Show("登录成功！");
        // 进入主界面
    }
    else
    {
        MessageBox.Show("用户名或密码错误");
    }
}

private async void RegisterButton_Click(object sender, EventArgs e)
{
    var username = txtNewUsername.Text;
    var password = txtNewPassword.Text;
    var inviteCode = txtInviteCode.Text;

    // 先验证邀请码
    var valid = await SupabaseService.ValidateInviteCodeAsync(inviteCode);
    if (!valid)
    {
        MessageBox.Show("邀请码无效或已使用");
        return;
    }

    // 注册
    var registered = await SupabaseService.RegisterUserAsync(username, password);
    if (registered)
    {
        MessageBox.Show("注册成功！");
        // 可选：标记邀请码为已使用
    }
    else
    {
        MessageBox.Show("用户名已存在或注册失败");
    }
}
```

---

## 🔄 第四阶段：防止 Supabase 项目休眠（保活机制）

Supabase 免费项目在 **7 天无活动** 后会暂停。为防止休眠，可以设置一个定时任务定期访问 API。

**最简单方法**：使用免费的 Cron-job 服务（如 [cron-job.org](https://cron-job.org)）定时（如每 6 小时）向你的 Supabase API 发送一个 GET 请求（例如查询一条公共数据）。不需要额外代码。

**或在 C# 客户端中**，程序启动时执行一次轻量查询（如 `SELECT 1`）即可。

---

## 📦 第五阶段：常见问题与排错

| 问题 | 原因与解决 |
|------|------------|
| 连接超时 | 检查网络，或使用代理；尝试更换 Supabase 区域（新加坡较优） |
| 403 权限错误 | RLS 策略未正确设置，检查 SQL 策略是否允许匿名访问 |
| 注册失败 | 用户名重复，或插入数据违反唯一约束 |
| 邀请码验证始终 false | 确认邀请码表中存在该码且 `used` 字段为 `false` |

---

## 🔐 安全建议（重要）

- **永远不要在客户端硬编码 service_role 密钥**，只用 anon key。
- 如果密码使用哈希存储，应使用 **bcrypt** 或 **Argon2**，不要使用简单哈希。
- 生产环境可考虑使用 Supabase 的 **Auth** 模块（内置 JWT），比自定义登录更安全。
- 定期备份 Supabase 数据（可导出为 SQL 或 CSV）。

---

## 📝 总结

这个方案完全满足你对“极少量数据、极低频率、高延迟容忍”的需求。Supabase 免费计划无需付费、无需续期，且 API 文档清晰，非常适合 C# 客户端集成。

你可以将本文档直接粘贴给 AI 编程助手（如 TraeCode），它能够根据此文档逐步生成对应的 C# 代码和配置。如需进一步调整（如增加邮箱验证、密码重置等），也可按需扩展。