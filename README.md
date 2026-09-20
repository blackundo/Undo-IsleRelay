# Undo-Isle Team Relay

Backend độc lập tương thích với `TeamRelayClient` của Undo-IsleLiveMap. Dịch vụ cung cấp REST API để tạo/vào/rời nhóm và SignalR hub để đồng bộ telemetry cùng map ping theo thời gian thực.

## Chạy trên máy phát triển

Yêu cầu .NET 8 SDK:

```powershell
dotnet run --project .\UndoIsle.TeamRelay.Server\UndoIsle.TeamRelay.Server.csproj
```

Kiểm tra tại `http://localhost:5000/health` hoặc địa chỉ được in ra trong terminal.

## Chạy trên VPS Ubuntu với Nginx Proxy Manager

1. Cài Docker Engine và Docker Compose plugin.
2. Xem tên Docker network mà container Nginx Proxy Manager đang sử dụng bằng `docker network ls`.
3. Sao chép `.env.example` thành `.env` và đặt `NPM_NETWORK` thành tên network đó.
4. Khởi động relay:

```bash
docker compose up -d --build
docker compose logs -f
```

Compose không publish cổng host nào và không chiếm cổng 80/443. Container chỉ mở cổng nội bộ `8080` trên network dùng chung với Nginx Proxy Manager.

Trong Nginx Proxy Manager, tạo Proxy Host:

- Domain Names: tên miền relay của bạn.
- Scheme: `http`.
- Forward Hostname/IP: `undo-isle-relay`.
- Forward Port: `8080`.
- Bật `Websockets Support`.
- Tạo chứng chỉ SSL, bật `Force SSL` và `HTTP/2 Support`.

Nếu Nginx Proxy Manager không kết nối được hostname container, kiểm tra hai container thực sự cùng network bằng `docker network inspect <tên-network>`.

## Kết nối ứng dụng

Sau khi `/health` hoạt động qua HTTPS, thay `TeamRelayClient.DefaultBaseUri` bằng URL mới, ví dụ:

```csharp
public static readonly Uri DefaultBaseUri = new("https://relay.example.com/");
```

## Phạm vi bản đầu

- Tối đa 15 thành viên mỗi nhóm.
- Thành viên hết hạn sau 35 giây không heartbeat; relay quét dọn mỗi 5 giây.
- Telemetry và map ping được kiểm tra giới hạn đầu vào.
- Protocol v2.2.2 hỗ trợ `ServerEndpoint`, `GetSnapshot`, room revision, `MemberRemovedV2` và `MapPingsChangedV2` để client tự phục hồi sau reconnect hoặc delta bị lỡ.
- Relay vẫn phát song song event legacy `MemberRemoved` và `MapPingsChanged`, nên client cũ tiếp tục hoạt động trong thời gian chuyển đổi.
- `/health` trả `protocol: 2` để kiểm tra nhanh VPS đã chạy đúng bản relay mới.
- Token thành viên được sinh ngẫu nhiên bằng bộ sinh số mật mã.
- Trạng thái lưu trong RAM; khi dịch vụ restart, các nhóm đang mở sẽ kết thúc.
- Một container relay duy nhất. Nếu cần chạy nhiều replica, phải thêm Redis backplane và kho trạng thái dùng chung.
