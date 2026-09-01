export async function onRequest(context) {
  // Grab the requested URL (e.g., https://your-app.pages.dev/api/trace)
  const url = new URL(context.request.url);
  
  // Point it to your Render backend (Replace this with your actual Render URL)
  const targetUrl = new URL(url.pathname + url.search, "https://c-sharp-visualizer.onrender.com/");
  
  // Forward the exact request (including POST body) to the backend
  const modifiedRequest = new Request(targetUrl, {
    method: context.request.method,
    headers: context.request.headers,
    body: context.request.body,
    redirect: "manual",
  });
  
  return fetch(modifiedRequest);
}
