using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MUD.Data
{
    public interface IRepository<TModel> where TModel : Entity
    {
        void Insert(TModel model);	

        bool Update(TModel model);	

        bool Delete(TModel model);

        TModel Get(string id);

        IEnumerable<TModel> GetAll();

        IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression);
    }
}