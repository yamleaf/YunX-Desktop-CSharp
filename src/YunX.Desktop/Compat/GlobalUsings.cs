// =====================================================================================
//  global using 补齐
//
//  两件不相关的事放在一起，都是为了「业务代码零条件编译」：
//
//  1. net48 的 ImplicitUsings 集合与 net8 **不同**。
//     net8.0-windows 的隐式 using 包含 System.Net.Http 与
//     System.Text.RegularExpressions，net48 的**都不包含**。
//     表现很迷惑：编译命令行里明明有 /reference:System.Net.Http.dll，
//     却报「找不到类型 HttpClient」—— 缺的不是引用，是 using。
//
//  2. YunX.Core.Compat 命名空间下的垫片（Math48 / Helpers / Hash48 / Rng48 …）
//     两个框架都有，业务代码统一用它们，不需要 #if。
//
//  这个文件在三个项目里各放一份（内容相同）—— global using 的作用域是
//  「编译单元所在项目」，跨项目不传递。
// =====================================================================================

#if NET48
global using System.Net.Http;
global using System.Text.RegularExpressions;
#endif

// 垫片在两个框架都可用，所以无条件引入
global using YunX.Core.Compat;
