using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Thalos.Sandbox.Docker;

/// <summary>Small helpers around Docker.DotNet's exceptions and filters.</summary>
internal static class DockerErrors
{
    /// <summary>Whether the engine answered 404.</summary>
    public static bool IsNotFound(Exception ex) => ex is DockerApiException { StatusCode: HttpStatusCode.NotFound };

    /// <summary>A one-line description of a library failure, for an <see cref="AgentError"/> detail and the log.</summary>
    public static string Describe(Exception ex, TimeSpan timeout) => ex switch
    {
        DockerApiException api => $"the Docker engine answered {(int)api.StatusCode}: {Trim(api.ResponseBody)}",
        OperationCanceledException => $"the Docker engine did not answer within {timeout.TotalSeconds:0} s",
        AggregateException { InnerException: { } inner } => Describe(inner, timeout),
        HttpRequestException or SocketException or IOException or TimeoutException => $"the Docker engine is unreachable: {ex.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>A filter of one key and one value.</summary>
    public static IDictionary<string, IDictionary<string, bool>> Filter(string key, string value) =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            [key] = new Dictionary<string, bool>(StringComparer.Ordinal) { [value] = true },
        };

    /// <summary>A label filter that requires every pair.</summary>
    public static IDictionary<string, IDictionary<string, bool>> Labels(params ReadOnlySpan<(string Key, string Value)> labels)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            values[$"{key}={value}"] = true;
        }

        return new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal) { ["label"] = values };
    }

    /// <summary>Inspects a container, or returns null on a 404.</summary>
    public static async ValueTask<ContainerInspectResponse?> TryInspectContainerAsync(DockerClient docker, string nameOrId, CancellationToken ct)
    {
        try
        {
            return await docker.Containers.InspectContainerAsync(nameOrId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    /// <summary>Inspects a volume, or returns null on a 404.</summary>
    public static async ValueTask<VolumeResponse?> TryInspectVolumeAsync(DockerClient docker, string name, CancellationToken ct)
    {
        try
        {
            return await docker.Volumes.InspectAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private static string Trim(string? body)
    {
        var text = (body ?? "").Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
