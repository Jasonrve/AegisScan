FROM golang:1.24 AS build
WORKDIR /src
COPY go.mod ./
COPY go.sum ./
RUN go mod download
COPY internal ./internal
COPY cmd ./cmd
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags='-s -w' -o /out/aegisscan ./cmd/aegisscan

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/aegisscan /aegisscan
USER nonroot:nonroot
EXPOSE 8080
ENTRYPOINT ["/aegisscan"]
