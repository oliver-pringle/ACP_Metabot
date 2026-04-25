export const INLINE_SIZE_LIMIT_BYTES = 50_000;

// TODO: when outputs grow large (e.g. dataLicense exports), add a /deliverables
// endpoint to ACP_Metabot.Api that stores the JSON and returns a public URL,
// then call it here when the inline payload exceeds INLINE_SIZE_LIMIT_BYTES
// instead of throwing.
export async function toDeliverable(
  jobId: string,
  payload: unknown
): Promise<string> {
  const json = JSON.stringify(payload);
  if (json.length <= INLINE_SIZE_LIMIT_BYTES) return json;
  throw new Error(
    `[deliverable] payload for job ${jobId} is ${json.length} bytes (> ${INLINE_SIZE_LIMIT_BYTES}). ` +
    `Add URL storage to ACP_Metabot.Api and wire it into deliverable.ts.`
  );
}
