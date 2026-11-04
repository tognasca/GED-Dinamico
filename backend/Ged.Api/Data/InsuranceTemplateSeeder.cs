using System.Text.Json;
using Ged.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ged.Api.Data;

/// <summary>Idempotent demonstration catalog. Never seed a production tenant automatically.</summary>
public static class InsuranceTemplateSeeder
{
    private static readonly Guid DemoTenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static async Task SeedAsync(GedDbContext db)
    {
        var templates = new (string Name, string[] Keywords, FieldDefinition[] Fields)[]
        {
            ("Seguro Auto - Apólice", ["apólice de seguro auto", "seguro automóvel", "seguro automotivo"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora", true),
                F("segurado", "Segurado", true), F("cpf_cnpj", "CPF/CNPJ"),
                F("placa", "Placa"), F("renavam", "RENAVAM"), F("chassi", "Chassi"),
                F("modelo_veiculo", "Modelo do veículo"), F("ano_modelo", "Ano/modelo"),
                F("inicio_vigencia", "Início da vigência", false, "date"),
                F("fim_vigencia", "Fim da vigência", false, "date"),
                F("premio_total", "Prêmio total", false, "currency"), F("franquia", "Franquia", false, "currency")
            ]),
            ("Seguro Auto - Sinistro", ["aviso de sinistro auto", "sinistro automóvel"], [
                F("numero_sinistro", "Número do sinistro", true), F("numero_apolice", "Número da apólice"),
                F("segurado", "Segurado"), F("placa", "Placa"), F("data_ocorrencia", "Data da ocorrência", false, "date"),
                F("tipo_ocorrencia", "Tipo de ocorrência"), F("descricao", "Descrição do evento"),
                F("status_sinistro", "Status do sinistro")
            ]),
            ("Seguro de Vida - Apólice", ["seguro de vida individual", "apólice de seguro de vida"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora", true),
                F("segurado", "Segurado", true), F("cpf", "CPF"), F("data_nascimento", "Data de nascimento", false, "date"),
                F("capital_segurado", "Capital segurado", false, "currency"),
                F("beneficiarios", "Beneficiários"), F("coberturas", "Coberturas"),
                F("inicio_vigencia", "Início da vigência", false, "date"),
                F("premio", "Prêmio", false, "currency")
            ]),
            ("Seguro de Vida - Sinistro", ["sinistro seguro de vida", "aviso de sinistro vida"], [
                F("numero_sinistro", "Número do sinistro", true), F("numero_apolice", "Número da apólice"),
                F("segurado", "Segurado"), F("beneficiario", "Beneficiário"),
                F("data_evento", "Data do evento", false, "date"),
                F("tipo_cobertura", "Tipo de cobertura"), F("capital_reclamado", "Capital reclamado", false, "currency")
            ]),
            ("Previdência Privada - Plano", ["plano de previdência privada", "previdência complementar", "PGBL", "VGBL"], [
                F("numero_proposta", "Número da proposta", true), F("entidade", "Entidade/seguradora", true),
                F("participante", "Participante", true), F("cpf", "CPF"),
                F("tipo_plano", "Tipo do plano (PGBL/VGBL)"), F("regime_tributario", "Regime tributário"),
                F("aporte", "Valor do aporte", false, "currency"),
                F("saldo", "Saldo acumulado", false, "currency"),
                F("beneficiarios", "Beneficiários"), F("data_adesao", "Data de adesão", false, "date")
            ]),
            ("Seguro Residencial", ["seguro residencial", "apólice residencial"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora"),
                F("segurado", "Segurado", true), F("endereco_imovel", "Endereço do imóvel"),
                F("coberturas", "Coberturas"), F("limite_indenizacao", "Limite de indenização", false, "currency"),
                F("premio_total", "Prêmio total", false, "currency"),
                F("inicio_vigencia", "Início da vigência", false, "date")
            ]),
            ("Seguro Empresarial", ["seguro empresarial", "apólice empresarial"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora"),
                F("razao_social", "Razão social", true), F("cnpj", "CNPJ"),
                F("endereco_risco", "Endereço do risco"), F("atividade", "Atividade"),
                F("coberturas", "Coberturas"), F("limite_indenizacao", "Limite de indenização", false, "currency"),
                F("premio_total", "Prêmio total", false, "currency")
            ]),
            ("Seguro Saúde", ["seguro saúde", "apólice saúde", "plano de saúde"], [
                F("numero_contrato", "Número do contrato", true), F("operadora", "Operadora/seguradora", true),
                F("titular", "Titular", true), F("cpf", "CPF"),
                F("acomodacao", "Acomodação"), F("abrangencia", "Abrangência"),
                F("coparticipacao", "Coparticipação"), F("inicio_vigencia", "Início da vigência", false, "date")
            ]),
            ("Seguro Viagem", ["seguro viagem", "assistência viagem"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora"),
                F("viajante", "Viajante", true), F("cpf", "CPF"), F("destino", "Destino"),
                F("data_inicio", "Início da viagem", false, "date"),
                F("data_fim", "Fim da viagem", false, "date"),
                F("cobertura_medica", "Cobertura médica", false, "currency")
            ]),
            ("Seguro de Responsabilidade Civil", ["seguro responsabilidade civil", "responsabilidade civil profissional"], [
                F("numero_apolice", "Número da apólice", true), F("seguradora", "Seguradora"),
                F("segurado", "Segurado", true), F("cpf_cnpj", "CPF/CNPJ"),
                F("atividade", "Atividade coberta"), F("limite_indenizacao", "Limite de indenização", false, "currency"),
                F("franquia", "Franquia", false, "currency"), F("inicio_vigencia", "Início da vigência", false, "date")
            ])
        };

        var existing = (await db.DocumentTypes.AsNoTracking()
            .Where(x => x.TenantId == DemoTenant)
            .Select(x => x.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, keywords, fields) in templates)
        {
            if (!existing.Add(name)) continue;
            db.DocumentTypes.Add(new DocumentType
            {
                TenantId = DemoTenant,
                Name = name,
                KeywordsJson = JsonSerializer.Serialize(keywords, JsonOptions.Default),
                FieldsJson = JsonSerializer.Serialize(fields, JsonOptions.Default)
            });
        }
        await db.SaveChangesAsync();
    }

    private static FieldDefinition F(string key, string label, bool required = false, string type = "text") =>
        new(key, label, type, required, Anchor: label);
}
