# Аналітичний сервіс

`Puluj.Analytics.Worker` мігрує й обслуговує власну схему `analytics`; її history — `analytics.__EFMigrationsHistory`. Воркер читає `raw_messages`, `sources`, `targets` і треки з основної схеми та будує незалежні індекси й звіти.

Воркер бере `raw_messages` за зростанням ID, старші за `Analytics:SafetyLag`, і в одній транзакції записує індекс та watermark. PostgreSQL advisory lock не дає двом інстансам виконувати один прогін. Після збою незакомічений пакет повторюється.

Схема містить `messages`, `track_firsts`, `runs` і `state`. Read-only endpoint `/api/admin/analytics/status` показує watermark, backlog, heartbeat і поточний прогін. Значення залежать від надходження даних та не гарантують повноту в реальному часі.

`POST /api/admin/analytics/reset` очищує незалежний індекс і `track_firsts`, після чого воркер перебудовує їх від watermark. Це destructive action для похідної аналітики; `raw_messages` і доменні результати processor не видаляються.
