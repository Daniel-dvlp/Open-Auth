using Microsoft.Extensions.DependencyInjection.Extensions;
using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Sesiones;

namespace YumaBilletera.Api.Shared;

public static class NucleoExtensions
{
    /// <summary>Servicios compartidos por los slices: pipeline de operaciones, señales, Escudo, bandeja de salida y autenticación reforzada.</summary>
    public static IServiceCollection AddNucleoBilletera(this IServiceCollection s, IHostEnvironment env)
    {
        s.AddScoped<ConstructorSenales>();
        s.AddScoped<ProcesadorOperacion>();
        s.AddOptions<LimitesOperacion>().BindConfiguration("Billetera:Limites");
        s.AddScoped<PublicadorEventos>();
        s.AddOptions<SesionOptions>().BindConfiguration("Sesion");
        s.AddScoped<EmisorSesion>();
        s.TryAddSingleton<IEscudoCliente, EscudoNoConfigurado>(); // falla cerrada hasta que se configure el adapter HTTP

        s.AddOptions<VerificacionOptions>().BindConfiguration("Verificacion");
        s.AddScoped<ServicioVerificacion>();
        s.TryAddSingleton<IVerificadorBiometrico, BiometriaSimulada>();
        if (env.IsDevelopment())
        {
            s.AddSingleton<BuzonOtpDesarrollo>();
            s.TryAddSingleton<IEnviadorOtp>(sp => sp.GetRequiredService<BuzonOtpDesarrollo>());
        }
        else s.TryAddSingleton<IEnviadorOtp, EnviadorOtpNoConfigurado>();
        return s;
    }
}
