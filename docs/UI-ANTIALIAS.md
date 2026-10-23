# 圆角抗锯齿：根因、统一改法、以及两个 WPF 陷阱

本文记录一次「控件边缘发脏」问题的完整排查，以及为了防止复发做的机械化检查。
凡是涉及圆角边框的地方，都按本文的规则写。

---

## 一、根因：`Border.BorderThickness` 在弧段上不是等宽的

`Border.BorderThickness` 画的是**沿边框内缩的一圈矩形环**。

- 在**直边**上：环的宽度就是 `BorderThickness`，1px 就是 1px，没有问题。
- 在**圆角弧段**上：环的外沿半径是 `R`、内沿半径是 `R - thickness`。
  两者沿法线方向的间距**仍然**是 1px —— 所以量厚度看不出问题。

真正的麻烦在于**渲染方式**：这一圈环是作为一个几何体用 `DrawGeometry` 描出来的，
它的抗锯齿发生在环的**两侧边缘**：

```
        ┌─ 外侧抗锯齿：描边色 × 背景色
        │
   ─────┤  1px 描边纯色
        │
        └─ 内侧抗锯齿：描边色 × 填充色   ← 这就是"脏"的来源
```

于是弧段上必然出现**两类**混色。外侧那层是必要的（边缘柔化），
内侧那层纯粹是副作用 —— 在深色主题 + 细描边 + 小半径（复选框 17×17 / R5）
的组合下，内侧混色占比很大，看起来就是「毛边」「脏圈」「两个框叠在一起」。

**这不是渲染 bug，是 `BorderThickness` 的几何定义决定的。**
只要用 `BorderThickness` 画圆角描边，就一定会出现。

---

## 二、统一改法：双层同心 Border

```
外层 Border：纯色 = 描边色，CornerRadius = R
内层 Border：纯色 = 填充色，Margin = 1，CornerRadius = R - 1
```

**两个半径必须同步减 1**，这样内层弧与外层弧严格平行，
`Margin=1` 保证同心（不是等比缩放）。

此时抗锯齿只发生在**各自与外层/背景的交界**：

```
   背景 ──[抗锯齿]── 外层描边纯色 ──[抗锯齿]── 内层填充纯色
          ↑ 正常边缘柔化          ↑ 落在外层纯色上，
                                    不会有"描边×填充"的中间色
```

### 半径对照表

| 控件 | 原尺寸 / 半径 | 外层 | 内层 |
|---|---|---|---|
| FlatCheck（复选框） | 17×17 / 5 | 17×17 / **5** | 15×15 / **4**（Margin=1） |
| InputBox / InputPassword | H36 / 8 | 36 / **8** | / **7** |
| ToggleSwitch 轨道 | 43×25 / 12.5 | 43×25 / **12.5** | / **11.5** |
| ComboBox 主体 / 弹层 | / 8 | / **8** | / **7** |
| 日志面板 / 悬浮卡片 | / 10 | / **10** | / **9** |
| 解析遮罩卡片 | / 12 | / **12** | / **11** |

### 复选框的实际写法

对勾画在**外层**（不进内层），坐标沿用原来的 17×17 画布，视觉位置不变：

```xml
<Grid Width="17" Height="17">
    <Border x:Name="boxStroke" CornerRadius="5"
            Background="{DynamicResource BorderHiBrush}" />
    <Border x:Name="boxFill" CornerRadius="4" Margin="1"
            Background="{DynamicResource SurfaceHiBrush}" />
    <Path x:Name="tick" Data="M 3.8,8.8 L 6.9,12 L 13.2,4.6" ... />
</Grid>
```

状态切换只改这两层的 `Background`，都是纯色，不引入渐变或发光。

> **为什么不用外发光（DropShadow / Effect）来"美化"聚焦态**：
> 发光元素必须比本体大，才能从圆角边缘露出来 —— 而露出的那圈正好会在弧上
> 再叠一次半透明色块，等于把刚修好的问题重新引入一遍。

---

## 三、两个 WPF 陷阱

### 3.1 `StaticResource` 解析不到跨字典的键 → 运行时才崩

`SeedPalette` 换主题色时是这么写的：

```csharp
private static void SetBrush(Application app, string key, Color c)
{
    var brush = new SolidColorBrush(c);
    brush.Freeze();
    app.Resources[key] = brush;   // ← 换的是**新对象**
}
```

`StaticResource` 在**解析时**就把引用绑定到了旧对象上，换色后不会跟随。
更麻烦的是它的失败方式：

| 赋值目标 | 表现 |
|---|---|
| `Background` / `BorderBrush` | 直接抛 `XamlParseException: 找不到资源` |
| **`Foreground`** | **不报错**，留一个 `DependencyProperty.UnsetValue`，<br>等到 TextBlock 真正渲染文本时才崩 |

后者的异常栈只能看到 `TextProperties.InitCommon`，完全看不出是资源问题 ——
排查成本极高。

**规则：凡引用会被运行时替换的键（颜色 / 画刷 / 渐变），一律 `DynamicResource`。**

不能改成 Dynamic 的例外：

| 场景 | 原因 |
|---|---|
| `BasedOn="{StaticResource Xxx}"` | `DynamicResource` 不支持 BasedOn，会抛异常 |
| `Style="{StaticResource Xxx}"` | 样式对象本身不会被替换 |
| `Converter={StaticResource Xxx}` | Converter 属性不接受 DynamicResource |
| 字体 / 图标几何 | 不参与换色，Static 更省 |

### 3.2 `{TemplateBinding BorderBrush}` 会取到 `UnsetValue`

`TemplateBinding` 是**单向、编译期**绑定的，它不做类型转换也不兜底。
如果控件的 `BorderBrush` 没有被显式赋值（或赋的值本身是 `UnsetValue`），
模板里的 `Border.Background` / `BorderBrush` 拿到的就是 `UnsetValue`，
而这两个属性不接受它 → 抛异常或渲染出意外结果。

**规则：模板内不要用 `TemplateBinding` 传画刷。**
直接在模板里写常量（或 `DynamicResource`），状态切换全部交给 `ControlTemplate.Triggers`。

```xml
<!-- ✗ 不要这样 -->
<Border Background="{TemplateBinding Background}"
        BorderBrush="{TemplateBinding BorderBrush}" />

<!-- ✓ 这样 -->
<Border x:Name="bdStroke" Background="{DynamicResource BorderHiBrush}" />
<Border x:Name="bdFill" Background="{TemplateBinding Background}" />
<!-- 状态切换： -->
<Trigger Property="IsKeyboardFocusWithin" Value="True">
    <Setter TargetName="bdStroke" Property="Background"
            Value="{DynamicResource AccentHiBrush}" />
</Trigger>
```

（`TemplateBinding Background` 本身是安全的 —— TextBox/CheckBox 的 Background
有默认值；风险在于**没人赋值的 BorderBrush**。）

---

## 四、机械化检查（防止复发）

### 4.1 静态断言（跑在 Smoke 里，每次构建都查）

`src/YunX.Smoke/Program.cs` 的 `TestXamlResourceTypes` 新增两条：

| 断言 | 判据 |
|---|---|
| **圆角描边未使用 BorderThickness** | 同一 Border 上 `CornerRadius > 0` 且 `BorderThickness` 为单值非 0 → 违规 |
| **颜色/画刷资源统一用 DynamicResource** | `{StaticResource XxxBrush/XxxColor}` 且该键是画刷/颜色定义 → 违规 |

> **⚠ 第一条断言的适用范围后来收窄了，见 4.3。**
> 它把「圆角 + BorderThickness」一律判为违规，但实测发现低对比描边
> （如  的  vs ）内侧那道过渡肉眼不可见，
> 逐个去改反而是纯风险。真正该看的是**描边与填充的对比度**。

白名单：`BorderThickness` 含逗号（单边分隔线不是环）、值为 0；
转换器名（含 `To`）不算画刷资源。

> 这两条断言第一次跑就抓出了 **6 处**历史遗留（两个日志面板/悬浮卡片、
> ComboBox 主体与弹层、ToggleSwitch 轨道、解析遮罩卡片）——
> 正是"只修了表面、其它地方还在用同一机制"的典型表现。

### 4.2 像素级审计（`--uiaudit`，需要时手动跑）

```powershell
YunX-Desktop.exe --uiaudit <输出目录>
```

做法：把控件用 `RenderTargetBitmap` 放大 **8 倍**渲染，沿弧段的多个斜率方向
采样颜色剖面，统计「稳定色块之间的混色像素团数量」。

| 实现 | 混色团数 | 结论 |
|---|---|---|
| 双层同心 Border（当前） | **1 组** | 只有最外沿抗锯齿，正常 |
| `BorderThickness`（对照实验，thickness=2 放大问题） | **2 组** | 外侧 + 内侧两道混色 |

同时验证**换色跟随**：渲染取色 → `SeedPalette.Apply` 换成红色 → 再渲染取色，
两次颜色必须不同。实测 `#4788F5 → #E03040`，说明 `DynamicResource` 生效。

> 注意：`--uiaudit` 每次新建控件采样。复用同一实例不行 ——
> 元素已入可视化树后，资源变更是**异步**触发的，
> 单纯 `UpdateLayout()` 不一定赶上。

---

## 五、检查清单

改任何带圆角的边框前，过一遍：

- [ ] 有 `CornerRadius > 0` 吗？→ 有，就不能用 `BorderThickness`
- [ ] 改成外层纯色 + 内层 `Margin="1"`，半径同步减 1
- [ ] 内层的 `Margin` 是否会让内容偏移？→ 需要把原 Padding 加 1 或调整内层 Padding
- [ ] 颜色引用是 `DynamicResource` 吗？
- [ ] 模板里有没有 `TemplateBinding BorderBrush`？→ 有，改成常量 + Trigger
- [ ] 跑一遍 Smoke 的两条 XAML 断言
- [ ] 视觉敏感的改动跑一次 `--uiaudit`
