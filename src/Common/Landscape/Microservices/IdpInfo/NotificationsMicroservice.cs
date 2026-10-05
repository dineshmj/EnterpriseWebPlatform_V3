namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

/// <summary>
/// Notifications context: tells staff about workflow progress and new work. It has no
/// MFE of its own; the Shell shows the notifications in its profile area.
/// </summary>
public static class NotificationsMicroservice
{
	// Notifications Subscriber to Notifications API M2M (consumes the workflow topics and
	// hands each event to the API, which decides who is told).
	public const string CLIENT_NAME_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M = "Notifications Subscriber to Notifications API M2M Client";

	public const string CLIENT_ID_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M = "Notifications.Subscriber.To.NotificationsApi.M2M.ClientID";


	public const string MICROSERVICE_API_BASE_URL = "https://notifications-api.dev.localhost:46377";
}