using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MUD.Data
{
    public interface IEntityProvider<TModel> where TModel : Entity
    {
        TModel Get(string id);

        IEnumerable<TModel> GetAll();

        IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression);
    }
}