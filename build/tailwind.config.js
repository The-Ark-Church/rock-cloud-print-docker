/**
 * Tailwind build config for the Cloud Print web UI.
 *
 * The UI used to pull Tailwind from cdn.tailwindcss.com on every page load.
 * That made the admin interface depend on a third-party host being reachable
 * and unchanged at runtime, and forced the CSP to allow an external script
 * origin. We now compile only the classes this page actually uses into
 * app.css, so the page ships everything it needs.
 *
 * app.js is scanned as well as index.html because much of the markup is built
 * there as HTML strings, and a class Tailwind never sees is left out of
 * app.css. theme.js is not listed: it only toggles the `dark` class, which
 * darkMode: 'class' handles without it appearing in content.
 */
module.exports = {
  darkMode: 'class',
  content: [
    "./Rock.CloudPrint.Service/wwwroot/index.html",
    "./Rock.CloudPrint.Service/wwwroot/app.js"
  ],
  theme: { extend: {} },
  plugins: []
};
