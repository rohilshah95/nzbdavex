import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const limit = Number(url.searchParams.get("limit") ?? "200");
    const attempts = await backendClient.getPlaybackAttempts(limit);
    return { attempts };
}
