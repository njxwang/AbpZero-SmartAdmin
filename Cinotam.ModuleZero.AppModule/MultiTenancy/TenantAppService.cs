using Abp.Application.Services.Dto;
using Abp.Authorization;
using Abp.AutoMapper;
using Abp.Extensions;
using Abp.MultiTenancy;
using Abp.Runtime.Security;
using Abp.UI;
using Cinotam.AbpModuleZero.Authorization;
using Cinotam.AbpModuleZero.Authorization.Roles;
using Cinotam.AbpModuleZero.Editions;
using Cinotam.AbpModuleZero.MultiTenancy;
using Cinotam.AbpModuleZero.Tools.DatatablesJsModels.GenericTypes;
using Cinotam.AbpModuleZero.Users;
using Cinotam.ModuleZero.AppModule.Features.Dto;
using Cinotam.ModuleZero.AppModule.Features.FeatureManager;
using Cinotam.ModuleZero.AppModule.MultiTenancy.Dto;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cinotam.ModuleZero.AppModule.MultiTenancy
{
    [AbpAuthorize(PermissionNames.PagesTenants)]
    public class TenantAppService : CinotamModuleZeroAppServiceBase, ITenantAppService
    {
        private readonly RoleManager _roleManager;
        private readonly EditionManager _editionManager;
        private readonly IAbpZeroDbMigrator _abpZeroDbMigrator;
        private readonly ICustomEditionManager _customEditionManager;

        public TenantAppService(
            RoleManager roleManager,
            EditionManager editionManager,
            IAbpZeroDbMigrator abpZeroDbMigrator,
            ICustomEditionManager customEditionManager)
        {
            _roleManager = roleManager;
            _editionManager = editionManager;
            _abpZeroDbMigrator = abpZeroDbMigrator;
            _customEditionManager = customEditionManager;
        }

        public ListResultDto<TenantListDto> GetTenants()
        {
            return new ListResultDto<TenantListDto>(
                TenantManager.Tenants
                    .OrderBy(t => t.TenancyName)
                    .ToList()
                    .MapTo<List<TenantListDto>>()
                );
        }

        public async Task SetFeatureValuesForTenant(CustomEditionInput input)
        {

            var allFeatureValuesForTenant = await TenantManager.GetFeatureValuesAsync(input.TenantId);


            var tenant = await TenantManager.GetByIdAsync(input.TenantId);

            foreach (var inputFeature in input.Features)
            {
                await TenantManager.SetFeatureValueAsync(tenant, inputFeature.Name, inputFeature.DefaultValue);
            }
        }

        public async Task<CustomEditionInput> GetFeaturesForTenant(int tenantId)
        {
            var tenant = await TenantManager.GetByIdAsync(tenantId);

            if (tenant.EditionId == null) throw new UserFriendlyException(L("NoEditionIsSetForTenant"));

            var edition = await _editionManager.FindByIdAsync(tenant.EditionId.Value);


            var mapped = edition.MapTo<CustomEditionInput>();

            mapped.TenantId = tenantId;

            mapped.Features = await _customEditionManager.GetAllFeatures(edition.Id, tenantId);

            return mapped;
        }


        public async Task ResetFeatures(int tenantId)
        {
            var tenant = await TenantManager.GetByIdAsync(tenantId);

            if (tenant.EditionId == null) throw new UserFriendlyException(L("NoEditionIsSetForTenant"));

            var editionFeatures = await _editionManager.GetFeatureValuesAsync(tenant.EditionId.Value);

            await TenantManager.SetFeatureValuesAsync(tenantId, editionFeatures.ToArray());

        }

        public ReturnModel<TenantListDto> GetTenantsTable(RequestModel<TenantListDto> input)
        {

            throw new System.NotImplementedException();
        }

        public async Task<EditionsForTenantOutput> GetEditionsForTenant(int tenantId)
        {
            var allEditions = _editionManager.Editions.ToList();
            var editionCList = new List<EditionDtoCustom>();
            foreach (var allEdition in allEditions)
            {
                var mappedEdition = allEdition.MapTo<EditionDtoCustom>();

                mappedEdition.IsEnabledForTenant = await IsThisEditionActive(tenantId, allEdition.Id);

                editionCList.Add(mappedEdition);
            }
            return new EditionsForTenantOutput()
            {
                Editions = editionCList,
                TenantId = tenantId
            };
        }

        private async Task<bool> IsThisEditionActive(int tenantId, int editionId)
        {
            var tenant = await TenantManager.GetByIdAsync(tenantId);

            return tenant.EditionId == editionId;

        }

        public async Task SetTenantEdition(SetTenantEditionInput input)
        {
            var tenant = await TenantManager.GetByIdAsync(input.TenantId);
            var edition = await _editionManager.FindByIdAsync(input.EditionId);
            if (edition != null)
            {
                tenant.EditionId = edition.Id;
            }
            await CurrentUnitOfWork.SaveChangesAsync();
        }
        public async Task CreateTenant(CreateTenantInput input)
        {
            //Create tenant
            var tenant = input.MapTo<Tenant>();
            tenant.ConnectionString = input.ConnectionString.IsNullOrEmpty()
                ? null
                : SimpleStringCipher.Instance.Encrypt(input.ConnectionString);

            var defaultEdition = await _editionManager.FindByNameAsync(EditionManager.DefaultEditionName);
            if (defaultEdition != null)
            {
                tenant.EditionId = defaultEdition.Id;
            }

            CheckErrors(await TenantManager.CreateAsync(tenant));
            await CurrentUnitOfWork.SaveChangesAsync(); //To get new tenant's id.

            //Create tenant database
            _abpZeroDbMigrator.CreateOrMigrateForTenant(tenant);

            //We are working entities of new tenant, so changing tenant filter
            using (CurrentUnitOfWork.SetTenantId(tenant.Id))
            {
                //Create static roles for new tenant
                CheckErrors(await _roleManager.CreateStaticRoles(tenant.Id));

                await CurrentUnitOfWork.SaveChangesAsync(); //To get static role ids

                //grant all permissions to admin role
                var adminRole = _roleManager.Roles.Single(r => r.Name == StaticRoleNames.Tenants.Admin);
                await _roleManager.GrantAllPermissionsAsync(adminRole);

                //Create admin user for the tenant
                var adminUser = User.CreateTenantAdminUser(tenant.Id, input.AdminEmailAddress, User.DefaultPassword);
                CheckErrors(await UserManager.CreateAsync(adminUser));
                await CurrentUnitOfWork.SaveChangesAsync(); //To get admin user's id

                //Assign admin user to role!
                CheckErrors(await UserManager.AddToRoleAsync(adminUser.Id, adminRole.Name));
                await CurrentUnitOfWork.SaveChangesAsync();
            }
        }
    }
}