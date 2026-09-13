/**
 * Tailwind build config for the Cloud Print web UI.
 *
 * The UI used to pull Tailwind from cdn.tailwindcss.com on every page load.
 * That made the admin interface depend on a third-party host being reachable
 * and unchanged at runtime, and forced the CSP to allow an external script
 * origin. We now compile only the classes this page actually uses into
 * app.css, so the page ships everything it needs.
 */
module.exports = {
  content: ["./Rock.CloudPrint.Service/wwwroot/index.html"],
  theme: { extend: {} },
  plugins: []
};
