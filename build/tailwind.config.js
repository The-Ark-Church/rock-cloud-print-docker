/**
 * Tailwind build config for the Cloud Print web UI.
 *
 * The UI used to pull Tailwind from cdn.tailwindcss.com at runtime, which
 * meant the admin page could not render on an isolated print VLAN with no
 * internet access, and forced the CSP to allow a third-party script origin.
 * We now compile only the classes this page actually uses into app.css.
 */
module.exports = {
  content: ["./Rock.CloudPrint.Service/wwwroot/index.html"],
  theme: { extend: {} },
  plugins: []
};
