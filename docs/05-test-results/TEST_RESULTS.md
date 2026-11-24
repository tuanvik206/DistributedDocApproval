# Kết quả kiểm tra và demo

Các kết quả dưới đây được ghi từ môi trường local với Docker Desktop, PostgreSQL 17, RabbitMQ, MinIO-compatible storage và .NET SDK 10.

## Kiểm tra mã nguồn

| Lệnh | Kết quả |
|---|---|
| `dotnet build DistributedDocApproval.slnx --no-restore -v:q` | Build thành công, 0 warning, 0 error sau thay đổi consumer recovery. |
| `dotnet test DistributedDocApproval.slnx --no-restore -v:q` | 15 test đạt sau thay đổi consumer recovery và bổ sung Document API integration: 8 unit + 7 integration, 0 failed, 0 skipped. |
| Docker Compose build | Năm image ứng dụng build thành công; các base image .NET 10. |
| `docker compose config -q` | Compose hợp lệ. |
| Clone sạch từ GitHub `main` | `dotnet build`, 15 tests, Compose config, build/up tám service, health checks và `/login` đều đạt; stack và volumes kiểm chứng đã được dọn. |

Integration tests dùng PostgreSQL Testcontainers. Coverage gồm đăng nhập/identity, upload và tải lại đúng bytes, Creator khác không thể đọc/download/update file, executable giả dạng PDF bị từ chối, quyết định tuần tự, sai thứ tự, thiếu lý do Reject, trùng quyết định, Admin không thể duyệt, hai quyết định cùng lúc chỉ một request ghi nhận, và notification Inbox idempotent.

## Kịch bản đầu cuối trên Compose

- Năm container ứng dụng cùng PostgreSQL, RabbitMQ và MinIO khởi động; readiness của bốn API đạt.
- Đăng nhập Blazor qua antiforgery; phiên trình duyệt có cookie `HttpOnly`.
- Upload PDF và tạo workflow hai approver. Approver không được gán nhận `404`; các bước được kích hoạt tuần tự; quyết định cuối cho Document và Workflow thành `Approved`.
- Reject có lý do cho kết quả `Rejected`, bước còn lại `Skipped`; Creator nhận notification `DocumentRejected`.
- Tạm dừng Notification API, submit và hoàn tất duyệt: nghiệp vụ vẫn thành công. Sau khi bật API, queue tạo đúng một notification assignment cho approver và một kết quả cho Creator.
- Tạm dừng RabbitMQ rồi submit: Document API trả `Pending`, Approval workflow chưa được tạo. Sau khi bật broker, Outbox phát lại và workflow được tạo.
- Kịch bản broker restart đầu tiên cho thấy Document consumer chưa gắn lại queue; đã sửa worker để lắng nghe `ChannelShutdownAsync`, đóng channel cũ và tạo consumer mới. Sau rebuild, khởi động lại RabbitMQ trong khi API vẫn chạy, nhận task, Approve và xác nhận Document cập nhật `Approved` mà không restart API.

## Ma trận nghiệm thu đã kiểm tra

| Mục | Bằng chứng |
|---|---|
| TC01 đăng nhập đúng | Identity login và cookie Blazor thành công. |
| TC06–TC10 workflow | Demo Approved hai bước và Rejected, kiểm tra thứ tự, lịch sử, trạng thái bước. |
| TC11–TC12 lặp và vai trò | Integration test: quyết định lặp trả Conflict, Admin bị Forbidden; test đồng thời chỉ một kết quả commit. |
| DT01 Notification outage | Quyết định lưu khi Notification API dừng; notification đến sau khi bật lại. |
| DT02 RabbitMQ outage | Submit được lưu qua Outbox; workflow và kết quả tiếp tục sau khi broker và consumer kết nối lại. |
| DT03 idempotency | Integration test Inbox xử lý event lặp mà không nhân bản notification. |
| DT06 health | Readiness của các API và dependency local đạt sau Compose startup. |
| DT08 clean clone | Clone `main` từ GitHub, build/test, chạy stack bằng cổng cô lập, xác nhận health và dọn tài nguyên test. |

Ảnh chụp trang đăng nhập từ giao diện local được lưu tại [01-login-page.png](../06-screenshots/01-login-page.png). Snapshot trực tiếp của Compose, health endpoints và RabbitMQ queues nằm trong [runtime-status.txt](runtime-status.txt). Các ảnh màn hình Compose, lịch sử workflow và RabbitMQ Management chưa được chụp; log kết quả ở trên chỉ ghi những gì đã chạy và không thay thế các ảnh này trong báo cáo học phần.
