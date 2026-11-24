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

## Còn thiếu

- Ảnh minh chứng Docker containers, màn hình workflow/audit và RabbitMQ Management.
- Commit đầu tiên và push lên `https://github.com/tuanvik206/DistributedDocApproval.git`. Branch local hiện chưa có commit; tên tác giả `Tuan` đã được xác nhận, còn thiếu email tác giả mà người dùng nói sẽ gửi riêng. Các file đã stage, `.env` không nằm trong stage. Không tạo commit giả với email hoặc ngày không được cung cấp.

Khi mỗi mốc tiếp theo hoàn tất, thêm một mục mới có ngày thực hiện, thay đổi, lệnh kiểm tra, ảnh và commit SHA thực tế.
