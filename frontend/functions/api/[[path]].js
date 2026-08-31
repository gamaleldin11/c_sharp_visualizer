export async function onRequest(context) {
  // Grab the requested URL (e.g., https://your-app.pages.dev/api/trace)
  const url = new URL(context.request.url);
  
  // Point it to your Fly.io backend (Replace this with your actual Fly.io URL)
  const targetUrl = new URL(url.pathname + url.search, "https://cs-visualizer.fly.dev/");
  
  // Forward the exact request (including POST body) to the backend
  const modifiedRequest = new Request(targetUrl, {
    method: context.request.method,
    headers: context.request.headers,
    body: context.request.body,
    redirect: "manual",
  });
  
  return fetch(modifiedRequest);
}
