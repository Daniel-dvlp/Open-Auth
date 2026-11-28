using System.Net;
using System.Text.Json;

namespace YumaBilletera.Api.Tests;

/// <summary>El contrato que recibe el equipo de frontend: documento OpenAPI con todas las rutas y nombres snake_case.</summary>
public class ContratoOpenApiTests
{
    [Fact]
    public async Task Documento_openapi_publica_las_rutas_del_contrato_en_snake_case()
    {
        using var f = new FabricaBilletera();
        var r = await f.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var rutas = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var esperada in new[]
                 {
                     "/v1/auth/login", "/v1/auth/otp", "/v1/auth/biometria", "/v1/auth/refresh", "/v1/cuentas/me", "/v1/cuentas/me/movimientos",
                     "/v1/destinatarios", "/v1/transferencias", "/v1/transferencias/{id}", "/v1/transferencias/{id}/biometria",
                     "/v1/pagos/qr", "/v1/pagos/servicios", "/v1/recargas", "/v1/dispositivos", "/v1/seguridad/clave",
                 })
            Assert.Contains(esperada, rutas);
        var esquemas = doc.RootElement.GetProperty("components").GetProperty("schemas").GetRawText();
        Assert.Contains("\"monto_cop\"", esquemas);
        Assert.Contains("\"id_dispositivo\"", esquemas);
        Assert.DoesNotContain("\"MontoCop\"", esquemas);
    }
}
