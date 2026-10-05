# AuthHub 运行镜像
#
# 构建与运行分两个阶段：SDK 阶段只用于编译，最终镜像只保留运行时与发布产物。
#
# 关于 SDK 版本：本仓库的解决方案文件是 .slnx，需要 SDK 9.0.200+ 才认识；
# 但运行目标框架是 net8.0（见 Directory.Build.props），因此运行时用 8.0 镜像。
# 这里刻意**按项目路径**发布而不是发布 .slnx，避免把"解决方案格式"这个
# 纯开发期细节耦合进镜像构建。

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# 先只复制工程文件与构建配置，让 restore 结果能被层缓存复用：
# 只要 *.csproj / *.props 没变，改业务代码就不会重新下载整个 NuGet 依赖树。
COPY Directory.Build.props ./
COPY src/AuthHub.Domain/AuthHub.Domain.csproj                         src/AuthHub.Domain/
COPY src/AuthHub.Application/AuthHub.Application.csproj               src/AuthHub.Application/
COPY src/AuthHub.Infrastructure/AuthHub.Infrastructure.csproj         src/AuthHub.Infrastructure/
COPY src/AuthHub.Api/AuthHub.Api.csproj                               src/AuthHub.Api/
RUN dotnet restore src/AuthHub.Api/AuthHub.Api.csproj

# 再复制源码并发布（不复制 tests/，镜像构建不需要测试工程）
COPY src/ src/
RUN dotnet publish src/AuthHub.Api/AuthHub.Api.csproj \
        -c Release \
        --no-restore \
        -o /app/publish \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# 基础镜像不含 curl，健康检查需要它；顺带建好日志目录并一次性把属主调整好。
# 注意这些必须在切到非 root 之前做，否则 apt 会因为权限不足失败。
#
# 关于 logs 目录：Serilog 的文件 Sink 写相对路径 logs/，容器里需要可写目录。
# 生产建议只保留 Console Sink（容器日志应交给 stdout 采集），
# 或用卷挂载并显式配置 Serilog__WriteTo__1__Args__path。
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /app/logs \
 && chown -R app:app /app

# 以非 root 运行：8.0 基础镜像已内置 app 用户（uid 1654）
USER app

COPY --from=build --chown=app:app /app/publish ./

# 容器内只监听 8080 的 HTTP；TLS 由前置网关终止。
# 网关场景记得同时设 AuthHub__Security__TrustForwardedHeaders=true，
# 否则审计日志里的来源 IP 会是网关地址。
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# 用 /health 做健康检查（该端点不依赖数据库，可安全用于存活探测）
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:8080/health || exit 1"]

ENTRYPOINT ["dotnet", "AuthHub.Api.dll"]
