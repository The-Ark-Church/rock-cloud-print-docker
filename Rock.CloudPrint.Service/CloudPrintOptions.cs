// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
namespace Rock.CloudPrint.Service;

/// <summary>
/// The options that will be used to initialize <see cref="ProxyWorker"/>.
/// </summary>
internal class CloudPrintOptions
{
    /// <summary>
    /// The base URL of the server to connect to, such as
    /// <c>https://rock.rocksolidchurchdemo.com</c>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The name of the proxy. If not specified then the computer name will
    /// be used instead.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The identifier of the Rock Device to connect as. Multiple proxies can
    /// connect as a single device and will be used for load balancing and/or
    /// failover.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Optional PIN or password that protects the web UI. Leave blank to
    /// disable authentication. Can also be supplied via the
    /// <c>Password</c> environment variable in <c>docker-compose.yml</c>.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// How long a print attempt may take before it is assumed the Rock server
    /// stopped waiting for it. Rock's check-in kiosk allows five seconds for the
    /// whole print operation and then shows the operator its own generic timeout
    /// message, so anything slower than this finished too late for the operator
    /// to have seen the real result. Set to zero to disable the check.
    /// </summary>
    public int SlowPrintMilliseconds { get; set; } = 5000;

    /// <summary>
    /// How long the connection may hear nothing at all from the server before
    /// it is treated as dead and rebuilt, in seconds. Set to zero to disable.
    ///
    /// This exists because a half-open connection produces no close frame: the
    /// socket stays open, the receive loop waits forever, and the proxy goes on
    /// believing it is connected while no print ever arrives. Silence is the
    /// only symptom available, so it must exceed the server's ping interval -
    /// that interval is logged once per connection so this can be checked.
    /// </summary>
    public int ConnectionIdleTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How often to send a WebSocket keepalive frame, in seconds. Zero disables
    /// it and leaves the runtime default.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>
    /// Whether print failures are reported to the Rock server. Off by default,
    /// and it does nothing at all until the Rock side exists - a Lava webhook, a
    /// workflow, and the communications inside that workflow. See the README.
    /// </summary>
    public bool NotificationsEnabled { get; set; }

    /// <summary>
    /// The full URL of the Lava webhook that receives failure notifications,
    /// for example
    /// <c>https://rock.example.com/Webhooks/Lava.ashx/notifications/cloud-print</c>.
    /// Must be HTTPS: the shared secret travels in a request header.
    /// </summary>
    public string NotificationUrl { get; set; } = string.Empty;

    /// <summary>
    /// The shared secret sent as the <c>X-CloudPrint-Token</c> header. The
    /// webhook compares it against a value held in Rock and rejects anything
    /// else with a 401.
    /// </summary>
    public string NotificationSecret { get; set; } = string.Empty;

    /// <summary>
    /// How long to stay quiet about a printer after reporting it, per kind of
    /// event. One printer failing ten times in a minute produces one
    /// notification; ten printers failing produce ten.
    /// </summary>
    public int NotificationCooldownMinutes { get; set; } = 5;
}
