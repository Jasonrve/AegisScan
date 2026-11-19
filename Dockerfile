FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV PATH="/root/.dotnet/tools:${PATH}"
COPY Directory.Build.props global.json ./
COPY .config ./.config
COPY src/AegisScan.Operator/AegisScan.Operator.csproj src/AegisScan.Operator/
RUN dotnet tool restore
RUN dotnet tool install --global KubeOps.Cli --version 14.0.0
RUN dotnet restore src/AegisScan.Operator/AegisScan.Operator.csproj
COPY src/AegisScan.Operator src/AegisScan.Operator
RUN dotnet publish src/AegisScan.Operator/AegisScan.Operator.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN useradd --uid 65532 --create-home --shell /usr/sbin/nologin aegisscan
COPY --from=build /out .
USER 65532
EXPOSE 8080
ENTRYPOINT ["dotnet", "AegisScan.Operator.dll"]
