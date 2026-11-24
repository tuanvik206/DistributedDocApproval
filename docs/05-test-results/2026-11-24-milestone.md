# Mốc triển khai — 2026-11-24

Đây là mốc bàn giao code hiện tại, ghi theo ngày thực hiện. Nội dung này không gán ngày hoặc lịch sử cho các tuần trước.

## Đã triển khai

- Hoàn thiện solution .NET 10 gồm Identity, Document, Approval, Notification và Blazor Client.Web.
- Hoàn tất EF migrations, database riêng cho từng backend, private object storage, role/resource authorization, workflow tuần tự và giao diện quản lý.
- Hoàn tất RabbitMQ, transactional Outbox/Inbox, notification, retry và khôi phục consumer khi broker restart.
- Hoàn tất Dockerfile, Compose, health checks, `.env.example`, README và báo cáo.
- Bổ sung test đồng thời cho quyết định cùng task và Document API upload/download/ownership; toàn bộ test đạt 15/15.

## Bằng chứng chạy

- `dotnet build DistributedDocApproval.slnx --no-restore -v:q`: thành công, 0 warning, 0 error.
- `dotnet test DistributedDocApproval.slnx --no-restore -v:q`: 15 passed, 0 failed, 0 skipped.
- `docker compose ... build`: năm image ứng dụng được build; tám service đều healthy.
- Smoke test đã chạy luồng Approved hai cấp, Rejected, cookie login HttpOnly, outage Notification API và RabbitMQ. Kịch bản chi tiết ở [TEST_RESULTS.md](TEST_RESULTS.md).
- Ảnh giao diện hiện có: [01-login-page.png](../06-screenshots/01-login-page.png).

## GitHub

- Commit `2353a6bf869403fb538c888d378b9fa7908c3640` (`feat: complete distributed document approval MVP`) được push lên `refs/heads/main`.
- Tác giả: `Tuan <tuanvik206@gmail.com>`. SHA trên GitHub khớp với `main` local.
- Không tạo commit hoặc ngày lịch sử giả cho các tuần trước; mốc tiếp theo cần ghi theo ngày thực hiện.

## Còn thiếu

- Ảnh minh chứng Docker containers, màn hình workflow/audit và RabbitMQ Management. Snapshot text runtime được giữ trong [runtime-status.txt](runtime-status.txt).

Khi mỗi mốc tiếp theo hoàn tất, thêm một mục mới có ngày thực hiện, thay đổi, lệnh kiểm tra, ảnh và commit SHA thực tế.
