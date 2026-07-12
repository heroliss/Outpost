# 多阶段构建：SDK 编译 → 运行时镜像只带发布产物（体积小、攻击面小）。
# 构建（在 Server~/OutpostServer/ 目录）：docker build -t outpost-server .
# 运行：docker run -p 8080:8080 -v outpost-data:/data outpost-server
#   -v 把 SQLite 库挂到命名卷，容器重建不丢榜（连接串经环境变量指向 /data）。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY OutpostServer.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# 监听 8080（云平台常规 HTTP 端口；WebSocket 走同端口 /ws）。
ENV ASPNETCORE_URLS=http://+:8080
# SQLite 落挂卷目录，容器重建榜单不丢。
ENV ConnectionStrings__Leaderboard="Data Source=/data/outpost.db"
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "OutpostServer.dll"]
