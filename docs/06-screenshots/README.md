# Ảnh minh chứng

| Ảnh | Nội dung |
|---|---|
| [01-login-page.png](01-login-page.png) | Trang đăng nhập thật của Blazor local. |
| [02-identity-openapi.png](02-identity-openapi.png) | OpenAPI JSON thật do Identity API phục vụ trong Development. |
| [03-rabbitmq-queues.png](03-rabbitmq-queues.png) | RabbitMQ Management thật; sáu queue đang chạy, queue thường có consumer. |
| [04-creator-dashboard.png](04-creator-dashboard.png) | Dashboard Creator sau khi hoàn tất các luồng demo Approved và Rejected. |
| [05-approver1-inbox.png](05-approver1-inbox.png) | Inbox Approver 1 với nhiệm vụ bước 1/2. |
| [06-approver2-inbox.png](06-approver2-inbox.png) | Inbox Approver 2 với nhiệm vụ bước 2/2 sau khi bước 1 được duyệt. |
| [07-workflow-two-step.png](07-workflow-two-step.png) | Timeline Approved có hai quyết định và nhận xét. |
| [08-creator-notifications.png](08-creator-notifications.png) | Inbox Creator nhận thông báo kết quả Approved và Rejected. |
| [09-workflow-rejected.png](09-workflow-rejected.png) | Timeline Rejected có lý do; bước 2 ở trạng thái Skipped. |
| [10-approved-document-detail.png](10-approved-document-detail.png) | Chi tiết file riêng tư và hai quyết định Approved trên cùng trang. |
| [11-notification-outage-queue.png](11-notification-outage-queue.png) | RabbitMQ giữ 3 event khi Notification API dừng; queue không có consumer. |
| [12-compose-notification-stopped.png](12-compose-notification-stopped.png) | Output thật `docker compose ps --all` trong outage; Notification API `Exited (0)`, các service khác healthy. |
| [13-notification-recovery-queue.png](13-notification-recovery-queue.png) | Queue sau restart: 0 message, consumer hoạt động lại. |
| [14-compose-all-healthy.png](14-compose-all-healthy.png) | Output thật `docker compose ps --all` sau recovery; đủ tám service healthy. |
| [15-notification-recovery-logs.png](15-notification-recovery-logs.png) | Log Docker thật cho ba event được consume sau khi Notification API restart. |

Ảnh giao diện được chụp trực tiếp từ stack local ngày 24/11/2026. Các luồng Approved/Rejected hiển thị hồ sơ demo đã chạy qua Creator và Approver.

Ảnh Compose là output `docker compose ps --all` thật được render trong trang local để đọc rõ; đây không phải ảnh chụp Docker Desktop UI. Snapshot containers, health endpoints và queue state nằm trong [runtime-status.txt](../05-test-results/runtime-status.txt); log đã lọc từ `docker logs` được lưu ở [notification-recovery.log](../05-test-results/notification-recovery.log). Kịch bản và kết quả ở [test results](../05-test-results/TEST_RESULTS.md).
